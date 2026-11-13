extern alias server;

using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using Game.Infrastructure;
using Game.Server.Rooms;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

const string issuer = "https://bot.local/";
const string audience = "game-bot-local";
string connectionString = Environment.GetEnvironmentVariable("BOT_CONNECTION_STRING")
    ?? throw new InvalidOperationException("BOT_CONNECTION_STRING is required. Use scripts/bot/run.ps1.");

await using (var source = PostgresServices.CreateDataSource(connectionString))
    await new SchemaMigrator(source, NullLogger<SchemaMigrator>.Instance).ApplyAsync();

await using var app = new BotApplication(connectionString, issuer, audience);
await using var match = await BotMatch.CreateAsync(app);
await match.PlayToCompletionAsync();

sealed class BotMatch : IAsyncDisposable
{
    private readonly HttpClient firstHttp;
    private readonly HttpClient secondHttp;
    private readonly HubConnection firstHub;
    private readonly HubConnection secondHub;
    private readonly Channel<JsonElement> firstStates = Channel.CreateUnbounded<JsonElement>();
    private readonly Channel<JsonElement> secondStates = Channel.CreateUnbounded<JsonElement>();
    private readonly Channel<JsonElement> firstRounds = Channel.CreateUnbounded<JsonElement>();
    private readonly Channel<JsonElement> secondRounds = Channel.CreateUnbounded<JsonElement>();
    private long sequence;
    private int firstEnergy;
    private int secondEnergy;

    private BotMatch(BotApplication app)
    {
        firstHttp = app.Player("bot|alpha");
        secondHttp = app.Player("bot|bravo");
        firstHub = app.Hub("bot|alpha");
        secondHub = app.Hub("bot|bravo");
        firstHub.On<JsonElement>("match.state.snapshot", value => firstStates.Writer.TryWrite(value));
        secondHub.On<JsonElement>("match.state.snapshot", value => secondStates.Writer.TryWrite(value));
        firstHub.On<JsonElement>("round.resolved", value => firstRounds.Writer.TryWrite(value));
        secondHub.On<JsonElement>("round.resolved", value => secondRounds.Writer.TryWrite(value));
    }

    public Guid MatchId { get; private set; }

    public static async Task<BotMatch> CreateAsync(BotApplication app)
    {
        var match = new BotMatch(app);
        await match.InitializeAsync();
        return match;
    }

    private async Task InitializeAsync()
    {
        using HttpResponseMessage createResponse = await firstHttp.PostAsJsonAsync("/api/rooms", new { visibility = "private" });
        createResponse.EnsureSuccessStatusCode();
        CreatedRoomResponse created = (await createResponse.Content.ReadFromJsonAsync<CreatedRoomResponse>())
            ?? throw new InvalidOperationException("The room response was empty.");

        using HttpResponseMessage joinResponse = await secondHttp.PostAsJsonAsync(
            $"/api/rooms/{created.Room.RoomId}/join", new { inviteCode = created.InviteCode });
        joinResponse.EnsureSuccessStatusCode();

        await Task.WhenAll(firstHub.StartAsync(), secondHub.StartAsync());

        using HttpResponseMessage firstReady = await firstHttp.PutAsJsonAsync(
            $"/api/rooms/{created.Room.RoomId}/ready", new { isReady = true });
        firstReady.EnsureSuccessStatusCode();
        using HttpResponseMessage secondReady = await secondHttp.PutAsJsonAsync(
            $"/api/rooms/{created.Room.RoomId}/ready", new { isReady = true });
        secondReady.EnsureSuccessStatusCode();
        RoomResponse room = (await secondReady.Content.ReadFromJsonAsync<RoomResponse>())
            ?? throw new InvalidOperationException("The ready response was empty.");
        MatchId = room.MatchId ?? throw new InvalidOperationException("The match did not start.");

        object join = new { type = "match.join", version = 1, matchId = MatchId };
        await Task.WhenAll(firstHub.InvokeAsync("JoinMatch", join), secondHub.InvokeAsync("JoinMatch", join));
        await Task.WhenAll(
            firstHub.InvokeAsync("SetMatchNickname", new
            {
                type = "match.nickname.set", version = 1, requestId = Guid.NewGuid(), matchId = MatchId, nickname = "BotAlpha"
            }),
            secondHub.InvokeAsync("SetMatchNickname", new
            {
                type = "match.nickname.set", version = 1, requestId = Guid.NewGuid(), matchId = MatchId, nickname = "BotBravo"
            }));

        JsonElement firstState = await ReadActiveStateAsync(firstStates);
        JsonElement secondState = await ReadActiveStateAsync(secondStates);
        sequence = firstState.GetProperty("sequence").GetInt64();
        firstEnergy = firstState.GetProperty("you").GetProperty("energy").GetInt32();
        secondEnergy = secondState.GetProperty("you").GetProperty("energy").GetInt32();
        Console.WriteLine($"Match {MatchId} started with two autonomous bots.");
    }

    public async Task PlayToCompletionAsync()
    {
        for (int round = 1; round <= 10; round++)
        {
            string[] firstPlan = BasicBotStrategy.ChoosePlan(firstEnergy);
            string[] secondPlan = BasicBotStrategy.ChoosePlan(secondEnergy);
            Console.WriteLine($"Round {round}: BotAlpha={firstPlan.Length} attack(s), BotBravo={secondPlan.Length} attack(s).");

            object firstMessage = CreatePlan(firstPlan);
            object secondMessage = CreatePlan(secondPlan);
            await Task.WhenAll(
                firstHub.InvokeAsync("SubmitRoundPlan", firstMessage),
                secondHub.InvokeAsync("SubmitRoundPlan", secondMessage));

            JsonElement firstResult = await ReadAsync(firstRounds);
            JsonElement secondResult = await ReadAsync(secondRounds);
            long nextSequence = firstResult.GetProperty("sequence").GetInt64();
            if (nextSequence != sequence + 1 || secondResult.GetProperty("sequence").GetInt64() != nextSequence)
                throw new InvalidOperationException("The bots received inconsistent round sequences.");

            sequence = nextSequence;
            string status = firstResult.GetProperty("status").GetString()
                ?? throw new InvalidOperationException("The round result omitted status.");
            if (status != "active")
            {
                using HttpResponseMessage history = await firstHttp.GetAsync($"/api/history/{MatchId}");
                history.EnsureSuccessStatusCode();
                Console.WriteLine($"Match {MatchId} finished after {round} round(s); sequence={sequence}; history=verified.");
                return;
            }

            firstEnergy = firstResult.GetProperty("you").GetProperty("energy").GetInt32();
            secondEnergy = secondResult.GetProperty("you").GetProperty("energy").GetInt32();
        }

        throw new InvalidOperationException("The match remained active after the maximum number of rounds.");
    }

    private object CreatePlan(string[] actions) => new
    {
        type = "round.plan.submit",
        version = 1,
        requestId = Guid.NewGuid(),
        matchId = MatchId,
        expectedSequence = sequence,
        actions
    };

    public async ValueTask DisposeAsync()
    {
        await firstHub.DisposeAsync();
        await secondHub.DisposeAsync();
        firstHttp.Dispose();
        secondHttp.Dispose();
    }

    private static async Task<JsonElement> ReadAsync(Channel<JsonElement> channel) =>
        await channel.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15));

    private static async Task<JsonElement> ReadActiveStateAsync(Channel<JsonElement> channel)
    {
        JsonElement state;
        do state = await ReadAsync(channel);
        while (state.GetProperty("status").GetString() != "active");
        return state;
    }
}

static class BasicBotStrategy
{
    public static string[] ChoosePlan(int energy)
    {
        if (energy <= 0)
            throw new InvalidOperationException("An active bot must have positive energy.");
        return Enumerable.Repeat("Attack", energy).ToArray();
    }
}

sealed class BotApplication(string connectionString, string issuer, string audience)
    : WebApplicationFactory<server::Program>
{
    private readonly SymmetricSecurityKey signingKey = new(RandomNumberGenerator.GetBytes(64));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseContentRoot(Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "src", "Game.Server")));
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:GameDatabase", connectionString);
        builder.UseSetting("Cors:AllowedOrigins:0", "http://localhost:5173");
        builder.UseSetting("Authentication:Authority", issuer);
        builder.UseSetting("Authentication:Audience", audience);
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["ConnectionStrings:GameDatabase"] = connectionString,
                ["Cors:AllowedOrigins:0"] = "http://localhost:5173",
                ["Authentication:Authority"] = issuer,
                ["Authentication:Audience"] = audience
            }));
        builder.ConfigureLogging(logging => logging.ClearProviders());
        builder.ConfigureTestServices(services => services.PostConfigure<JwtBearerOptions>(
            JwtBearerDefaults.AuthenticationScheme,
            options =>
            {
                var configuration = new OpenIdConnectConfiguration { Issuer = issuer };
                configuration.SigningKeys.Add(signingKey);
                options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = issuer,
                    ValidateAudience = true,
                    ValidAudience = audience,
                    ValidateLifetime = true,
                    RequireSignedTokens = true,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = signingKey,
                    ValidAlgorithms = [SecurityAlgorithms.HmacSha256]
                };
            }));
    }

    public HttpClient Player(string subject)
    {
        HttpClient client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token(subject));
        return client;
    }

    public HubConnection Hub(string subject)
    {
        using HttpClient client = CreateClient();
        return new HubConnectionBuilder()
            .WithUrl(new Uri(client.BaseAddress!, "/hubs/game"), options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => Server.CreateHandler();
                options.AccessTokenProvider = () => Task.FromResult<string?>(Token(subject));
            })
            .Build();
    }

    private string Token(string subject) => new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
        issuer,
        audience,
        [new Claim("sub", subject)],
        expires: DateTime.UtcNow.AddMinutes(15),
        signingCredentials: new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256)));
}
