using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
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
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Game.Postgres.Tests;

[Trait("Category", "PostgreSQL")]
public sealed class HttpSignalRTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [PostgresFact] public async Task TwoPlayersCompleteRealRestAndSignalRFlowWithDurableResult()
    {
        await using var db = await fixture.CreateDatabaseAsync(); await using var app = new GameApplication(db.ConnectionString);
        using var alice = app.Player("auth0|http-alice"); using var bob = app.Player("auth0|http-bob");
        var createdResponse = await alice.PostAsJsonAsync("/api/rooms", new { visibility = "private" });
        createdResponse.EnsureSuccessStatusCode();
        var created = (await createdResponse.Content.ReadFromJsonAsync<CreatedRoomResponse>())!;
        (await bob.PostAsJsonAsync($"/api/rooms/{created.Room.RoomId}/join", new { inviteCode = created.InviteCode })).EnsureSuccessStatusCode();
        await using var hubA = app.Hub("auth0|http-alice"); await using var hubB = app.Hub("auth0|http-bob");
        var statesA = Channel.CreateUnbounded<JsonElement>(); var statesB = Channel.CreateUnbounded<JsonElement>();
        var roundsA = Channel.CreateUnbounded<JsonElement>(); var roundsB = Channel.CreateUnbounded<JsonElement>();
        var roomStarts = Channel.CreateUnbounded<JsonElement>();
        hubA.On<JsonElement>("match.state.snapshot", message => statesA.Writer.TryWrite(message));
        hubB.On<JsonElement>("match.state.snapshot", message => statesB.Writer.TryWrite(message));
        hubA.On<JsonElement>("round.resolved", message => roundsA.Writer.TryWrite(message));
        hubB.On<JsonElement>("round.resolved", message => roundsB.Writer.TryWrite(message));
        hubA.On<JsonElement>("room.match.started", message => roomStarts.Writer.TryWrite(message));
        await Task.WhenAll(hubA.StartAsync(), hubB.StartAsync());
        var roomMessage = new { type = "room.join", version = 1, roomId = created.Room.RoomId };
        await hubA.InvokeAsync("JoinRoom", roomMessage); await hubB.InvokeAsync("JoinRoom", roomMessage);
        (await alice.PutAsJsonAsync($"/api/rooms/{created.Room.RoomId}/ready", new { isReady = true })).EnsureSuccessStatusCode();
        var readyResponse = await bob.PutAsJsonAsync($"/api/rooms/{created.Room.RoomId}/ready", new { isReady = true }); readyResponse.EnsureSuccessStatusCode();
        var ready = (await readyResponse.Content.ReadFromJsonAsync<RoomResponse>())!; Guid matchId = ready.MatchId!.Value;
        Assert.Equal(matchId, (await NextAsync(roomStarts)).GetProperty("matchId").GetGuid());
        var join = new { type = "match.join", version = 1, matchId };
        await hubA.InvokeAsync("JoinMatch", join); await hubB.InvokeAsync("JoinMatch", join);
        await hubA.InvokeAsync("SetMatchNickname", new { type = "match.nickname.set", version = 1, requestId = Guid.NewGuid(), matchId, nickname = "Alice" });
        await hubB.InvokeAsync("SetMatchNickname", new { type = "match.nickname.set", version = 1, requestId = Guid.NewGuid(), matchId, nickname = "Bob" });
        var stateA = await ActiveAsync(statesA); var stateB = await ActiveAsync(statesB);
        int resolved = 0;
        while (stateA.GetProperty("status").GetString() == "active")
        {
            long sequence = stateA.GetProperty("sequence").GetInt64();
            var a = new { type = "round.plan.submit", version = 1, requestId = Guid.NewGuid(), matchId, expectedSequence = sequence,
                actions = Enumerable.Repeat("Attack", stateA.GetProperty("you").GetProperty("energy").GetInt32()).ToArray() };
            var b = new { type = "round.plan.submit", version = 1, requestId = Guid.NewGuid(), matchId, expectedSequence = sequence,
                actions = Enumerable.Repeat("Reserve", stateB.GetProperty("you").GetProperty("energy").GetInt32()).ToArray() };
            await Task.WhenAll(hubA.InvokeAsync("SubmitRoundPlan", a), hubB.InvokeAsync("SubmitRoundPlan", b));
            stateA = await NextAsync(roundsA); stateB = await NextAsync(roundsB); resolved++;
            Assert.Equal(stateA.GetProperty("sequence").GetInt64(), stateB.GetProperty("sequence").GetInt64());
            Assert.DoesNotContain("actions", stateA.GetRawText(), StringComparison.OrdinalIgnoreCase);
        }
        var stored = (await db.Store.GetAsync(matchId))!;
        Assert.Equal(Game.Domain.MatchStatus.Finished, stored.State.Status);
        Assert.Equal(stored.State.FirstPlayer.PlayerId, stored.State.WinnerId);
        Assert.Equal(resolved, stored.State.SequenceNumber);
        Assert.Equal((long)resolved + 1, await db.ScalarAsync<long>("SELECT count(*) FROM match_events"));
        Assert.Equal(0L, await db.ScalarAsync<long>("SELECT count(*) FROM pending_round_plans"));
        var room = (await alice.GetFromJsonAsync<RoomResponse>($"/api/rooms/{created.Room.RoomId}"))!;
        Assert.Equal("Partida finalizada", room.Status);
        (await alice.DeleteAsync($"/api/rooms/{created.Room.RoomId}/membership")).EnsureSuccessStatusCode();
    }

    [PostgresFact] public async Task HttpAndHubEnforceAuthenticatedHumanParticipant()
    {
        await using var db = await fixture.CreateDatabaseAsync(); await using var app = new GameApplication(db.ConnectionString);
        using var anonymous = app.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/rooms", new { visibility = "public" })).StatusCode);
        using var machine = app.Player("machine@clients");
        Assert.Equal(HttpStatusCode.Forbidden, (await machine.PostAsJsonAsync("/api/rooms", new { visibility = "public" })).StatusCode);
        var game = new LiveMatch(db); await game.CreateAsync();
        await using var outsider = app.Hub("auth0|outsider"); await outsider.StartAsync();
        var error = await Assert.ThrowsAsync<Microsoft.AspNetCore.SignalR.HubException>(() => outsider.InvokeAsync("JoinMatch", new { type = "match.join", version = 1, matchId = game.Initial.MatchId }));
        Assert.Contains("not_match_participant", error.Message);
        Assert.Equal(0L, await db.ScalarAsync<long>("SELECT count(*) FROM match_commands"));
    }

    [PostgresFact] public async Task RestReportsProblemDetailsWithoutConfirmingFailedCreation()
    {
        await using var db = await fixture.CreateDatabaseAsync(); await using var app = new GameApplication(db.ConnectionString);
        using var alice = app.Player("auth0|failure-alice"); using var bob = app.Player("auth0|failure-bob");
        var response = await alice.PostAsJsonAsync("/api/rooms", new { visibility = "public" }); response.EnsureSuccessStatusCode();
        var created = (await response.Content.ReadFromJsonAsync<CreatedRoomResponse>())!;
        (await bob.PostAsJsonAsync($"/api/rooms/{created.Room.RoomId}/join", new { inviteCode = (string?)null })).EnsureSuccessStatusCode();
        (await alice.PutAsJsonAsync($"/api/rooms/{created.Room.RoomId}/ready", new { isReady = true })).EnsureSuccessStatusCode();
        await db.ExecuteAsync("""
            CREATE FUNCTION injected_http_failure() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'injected failure' USING ERRCODE='23514'; END; $$;
            CREATE TRIGGER injected_http_event_failure BEFORE INSERT ON match_events FOR EACH ROW EXECUTE FUNCTION injected_http_failure();
            """);
        var failed = await bob.PutAsJsonAsync($"/api/rooms/{created.Room.RoomId}/ready", new { isReady = true });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        Assert.Equal("application/problem+json", failed.Content.Headers.ContentType!.MediaType);
        var problem = (await failed.Content.ReadFromJsonAsync<JsonElement>());
        Assert.Equal("database_unavailable", problem.GetProperty("code").GetString());
        Assert.DoesNotContain("auth0|", problem.GetRawText()); Assert.DoesNotContain("Password", problem.GetRawText());
        Assert.Equal(0L, await db.ScalarAsync<long>("SELECT count(*) FROM matches"));
        await db.ExecuteAsync("DROP TRIGGER injected_http_event_failure ON match_events");
        (await bob.PutAsJsonAsync($"/api/rooms/{created.Room.RoomId}/ready", new { isReady = true })).EnsureSuccessStatusCode();
        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT count(*) FROM matches"));
    }

    [PostgresFact] public async Task NewHostResumesPendingPlanAndCompletesRoundExactlyOnce()
    {
        await using var db=await fixture.CreateDatabaseAsync();var key=new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(64));Guid matchId;
        await using(var first=new GameApplication(db.ConnectionString,key))
        {
            using var alice=first.Player("auth0|restart-alice");using var bob=first.Player("auth0|restart-bob");
            var response=await alice.PostAsJsonAsync("/api/rooms",new{visibility="public"});response.EnsureSuccessStatusCode();var room=(await response.Content.ReadFromJsonAsync<CreatedRoomResponse>())!;
            (await bob.PostAsJsonAsync($"/api/rooms/{room.Room.RoomId}/join",new{inviteCode=(string?)null})).EnsureSuccessStatusCode();
            (await alice.PutAsJsonAsync($"/api/rooms/{room.Room.RoomId}/ready",new{isReady=true})).EnsureSuccessStatusCode();var ready=await bob.PutAsJsonAsync($"/api/rooms/{room.Room.RoomId}/ready",new{isReady=true});ready.EnsureSuccessStatusCode();matchId=(await ready.Content.ReadFromJsonAsync<RoomResponse>())!.MatchId!.Value;
            await using var hubA=first.Hub("auth0|restart-alice");await using var hubB=first.Hub("auth0|restart-bob");await Task.WhenAll(hubA.StartAsync(),hubB.StartAsync());
            var join=new{type="match.join",version=1,matchId};await hubA.InvokeAsync("JoinMatch",join);await hubB.InvokeAsync("JoinMatch",join);
            await hubA.InvokeAsync("SetMatchNickname",new{type="match.nickname.set",version=1,requestId=Guid.NewGuid(),matchId,nickname="Alice"});
            await hubB.InvokeAsync("SetMatchNickname",new{type="match.nickname.set",version=1,requestId=Guid.NewGuid(),matchId,nickname="Bob"});
            await hubA.InvokeAsync("SubmitRoundPlan",new{type="round.plan.submit",version=1,requestId=Guid.NewGuid(),matchId,expectedSequence=0,actions=new[]{"Attack"}});
        }
        await using(var restarted=new GameApplication(db.ConnectionString,key))
        {
            await using var hubA=restarted.Hub("auth0|restart-alice");await using var hubB=restarted.Hub("auth0|restart-bob");var statesA=Channel.CreateUnbounded<JsonElement>();var statesB=Channel.CreateUnbounded<JsonElement>();var rounds=Channel.CreateUnbounded<JsonElement>();
            hubA.On<JsonElement>("match.state.snapshot",m=>statesA.Writer.TryWrite(m));hubB.On<JsonElement>("match.state.snapshot",m=>statesB.Writer.TryWrite(m));hubB.On<JsonElement>("round.resolved",m=>rounds.Writer.TryWrite(m));
            await Task.WhenAll(hubA.StartAsync(),hubB.StartAsync());var resume=new{type="match.resume",version=1,matchId=(Guid?)null,lastSequence=(long?)null};await hubA.InvokeAsync("ResumeMatch",resume);await hubB.InvokeAsync("ResumeMatch",resume);
            var stateA=await NextAsync(statesA);var stateB=await NextAsync(statesB);Assert.Equal(matchId,stateA.GetProperty("matchId").GetGuid());Assert.True(stateA.GetProperty("hasSubmittedPlan").GetBoolean());Assert.False(stateB.GetProperty("hasSubmittedPlan").GetBoolean());
            await hubB.InvokeAsync("SubmitRoundPlan",new{type="round.plan.submit",version=1,requestId=Guid.NewGuid(),matchId,expectedSequence=0,actions=new[]{"Attack"}});Assert.Equal(1,(await NextAsync(rounds)).GetProperty("sequence").GetInt64());
        }
        Assert.Equal(1L,await db.ScalarAsync<long>("SELECT count(*) FROM match_events WHERE match_id=$1 AND event_type='round.resolved'",matchId));Assert.Equal(1,(await db.Store.GetAsync(matchId))!.State.SequenceNumber);
    }

    private static async Task<JsonElement> NextAsync(Channel<JsonElement> channel) =>
        await channel.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
    private static async Task<JsonElement> ActiveAsync(Channel<JsonElement> channel)
    { JsonElement message; do { message = await NextAsync(channel); } while (message.GetProperty("status").GetString() != "active"); return message; }

    // Real ASP.NET authentication middleware, HTTP and SignalR; only the issuer/key are test-local.
    // Auth0 setup and secrets in the production app are never changed.
    private sealed class GameApplication(string connectionString, SymmetricSecurityKey? sharedKey = null) : WebApplicationFactory<Program>
    {
        private readonly SymmetricSecurityKey key = sharedKey ?? new(RandomNumberGenerator.GetBytes(64));
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            // Host settings are available during the minimal entry point, before Build().
            builder.UseSetting("ConnectionStrings:GameDatabase", connectionString);
            builder.UseSetting("Cors:AllowedOrigins:0", "http://localhost:5173");
            builder.UseSetting("Authentication:Authority", "https://identity.test/");
            builder.UseSetting("Authentication:Audience", "phase2b-tests");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:GameDatabase"] = connectionString,
                ["Cors:AllowedOrigins:0"] = "http://localhost:5173",
                ["Authentication:Authority"] = "https://identity.test/",
                ["Authentication:Audience"] = "phase2b-tests"
            }));
            builder.ConfigureLogging(logging => logging.ClearProviders());
            builder.ConfigureTestServices(services => services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                var configuration = new OpenIdConnectConfiguration { Issuer = "https://identity.test/" };
                configuration.SigningKeys.Add(key);
                options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true, ValidIssuer = configuration.Issuer,
                    ValidateAudience = true, ValidAudience = "phase2b-tests",
                    ValidateLifetime = true, RequireSignedTokens = true, ValidateIssuerSigningKey = true,
                    IssuerSigningKey = key, ValidAlgorithms = [SecurityAlgorithms.HmacSha256]
                };
            }));
        }
        private string Token(string subject) => new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            issuer: "https://identity.test/", audience: "phase2b-tests", claims: [new Claim("sub", subject)],
            expires: DateTime.UtcNow.AddMinutes(10), signingCredentials: new(key, SecurityAlgorithms.HmacSha256)));
        public HttpClient Player(string subject)
        {
            var client = CreateClient(); client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token(subject)); return client;
        }
        public HubConnection Hub(string subject)
        {
            // LongPolling runs through TestServer; no external listening ports or Auth0 calls.
            using var client = CreateClient();
            return new HubConnectionBuilder().WithUrl(new Uri(client.BaseAddress!, "/hubs/game"), options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => Server.CreateHandler();
                options.AccessTokenProvider = () => Task.FromResult<string?>(Token(subject));
            }).Build();
        }
    }
}
