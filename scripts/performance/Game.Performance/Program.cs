extern alias server;
using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using Game.Infrastructure;
using Game.Server.Administration;
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
using Npgsql;

const string issuer = "https://performance.local/";
const string audience = "game-performance-local";
string connectionString = Environment.GetEnvironmentVariable("PERFORMANCE_CONNECTION_STRING")
    ?? throw new InvalidOperationException("PERFORMANCE_CONNECTION_STRING is required.");
string outputPath = Environment.GetEnvironmentVariable("PERFORMANCE_OUTPUT")
    ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../results/phase5a-results.json"));

await using (NpgsqlDataSource source = PostgresServices.CreateDataSource(connectionString))
    await new SchemaMigrator(source, NullLogger<SchemaMigrator>.Instance).ApplyAsync();

await using var app = new PerformanceApplication(connectionString, issuer, audience);
var run = new PerformanceRun();
Console.WriteLine("Warm-up: one unmeasured match...");
await using (MatchClient warmup = await MatchClient.CreateAsync(app, "warmup", null))
{
    await warmup.ResolveRoundAsync(null);
    await warmup.AbandonAsync(null);
}
run.WarmupCompleted = true;

await RunMatchScenarioAsync("A-baseline", 1, 5, run, app);
await RunMatchScenarioAsync("B-moderate", 10, 3, run, app);
bool moderateSafe = run.Samples.Where(x => x.Scenario == "B-moderate").All(x => x.Success)
    && run.ScenarioResources.Last(x => x.Scenario == "B-moderate").PeakWorkingSetMiB < 2048;
if (moderateSafe)
    await RunMatchScenarioAsync("C-high", 20, 2, run, app);
else
    run.SkippedScenarios.Add("C-high: moderate load reported errors or excessive memory.");

await RunPersistenceAsync(run, app);
await RunReconnectAsync(run, app);

await Task.Delay(500);
run.Database = await ValidateDatabaseAsync(connectionString);
run.RemainingConnections = app.Services.GetRequiredService<ConnectionRegistry>().List().Count;
run.Environment = new EnvironmentInfo(
    Environment.OSVersion.ToString(), Environment.Version.ToString(), Environment.ProcessorCount,
    GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024d / 1024d,
    Process.GetCurrentProcess().ProcessName, "Microsoft.AspNetCore.TestHost + SignalR LongPolling", "PostgreSQL 17.11 (Docker Compose)");
run.FinishedAtUtc = DateTimeOffset.UtcNow;
run.Aggregates = Aggregate(run.Samples);
Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(run, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Results: {outputPath}");
Console.WriteLine($"Samples={run.Samples.Count}; errors={run.Samples.Count(x => !x.Success)}; activeMatches={run.Database.ActiveMatches}; connections={run.RemainingConnections}");

static async Task RunMatchScenarioAsync(string scenario, int matchCount, int rounds, PerformanceRun run, PerformanceApplication app)
{
    Console.WriteLine($"{scenario}: {matchCount} concurrent matches, up to {rounds} rounds...");
    using var resources = new ResourceProbe(scenario);
    Stopwatch wall = Stopwatch.StartNew();
    MatchClient[] matches = await Task.WhenAll(Enumerable.Range(0, matchCount)
        .Select(index => MatchClient.CreateAsync(app, $"{scenario}-{index}", run.Samples)));
    try
    {
        for (int round = 0; round < rounds; round++)
            await Task.WhenAll(matches.Where(x => x.Active).Select(x => x.ResolveRoundAsync(run.Samples)));
        await Task.WhenAll(matches.Where(x => x.Active).Select(x => x.AbandonAsync(run.Samples)));
        run.CompletedMatches.AddRange(matches.Select(x => new CompletedMatch(x.MatchId, x.AliceSubject)));
    }
    finally
    {
        await Task.WhenAll(matches.Select(x => x.DisposeAsync().AsTask()));
    }
    wall.Stop();
    run.ScenarioResources.Add(resources.Finish(matchCount * 2, matchCount, wall.Elapsed));
}

static async Task RunPersistenceAsync(PerformanceRun run, PerformanceApplication app)
{
    const string scenario = "D-persistence";
    Console.WriteLine($"{scenario}: history, detail and replay...");
    using var resources = new ResourceProbe(scenario);
    Stopwatch wall = Stopwatch.StartNew();
    foreach (CompletedMatch match in run.CompletedMatches.Take(25))
    {
        using HttpClient client = app.Player(match.Subject);
        await MeasureHttpAsync(run.Samples, scenario, "http.history", () => client.GetAsync("/api/history?page=1&pageSize=20"));
        await MeasureHttpAsync(run.Samples, scenario, "http.detail", () => client.GetAsync($"/api/history/{match.MatchId}"));
        await MeasureHttpAsync(run.Samples, scenario, "http.replay", () => client.GetAsync($"/api/history/{match.MatchId}/events"));
    }
    wall.Stop();
    run.ScenarioResources.Add(resources.Finish(25, 0, wall.Elapsed));
}

static async Task RunReconnectAsync(PerformanceRun run, PerformanceApplication app)
{
    const string scenario = "E-reconnect";
    Console.WriteLine($"{scenario}: five active matches...");
    using var resources = new ResourceProbe(scenario);
    Stopwatch wall = Stopwatch.StartNew();
    MatchClient[] matches = await Task.WhenAll(Enumerable.Range(0, 5)
        .Select(index => MatchClient.CreateAsync(app, $"reconnect-{index}", run.Samples)));
    try
    {
        foreach (MatchClient match in matches)
            await match.ReconnectAliceAsync(run.Samples);
        await Task.WhenAll(matches.Select(x => x.AbandonAsync(run.Samples)));
        run.CompletedMatches.AddRange(matches.Select(x => new CompletedMatch(x.MatchId, x.AliceSubject)));
    }
    finally { await Task.WhenAll(matches.Select(x => x.DisposeAsync().AsTask())); }
    wall.Stop();
    run.ScenarioResources.Add(resources.Finish(10, 5, wall.Elapsed));
}

static async Task MeasureHttpAsync(List<Sample> samples, string scenario, string operation, Func<Task<HttpResponseMessage>> action)
{
    Stopwatch timer = Stopwatch.StartNew();
    try
    {
        using HttpResponseMessage response = await action();
        timer.Stop();
        Perf.Record(samples, new(scenario, operation, timer.Elapsed.TotalMilliseconds, response.IsSuccessStatusCode,
            (int)response.StatusCode, response.StatusCode == System.Net.HttpStatusCode.TooManyRequests, false,
            response.IsSuccessStatusCode ? null : await response.Content.ReadAsStringAsync()));
    }
    catch (TimeoutException ex) { timer.Stop(); Perf.Record(samples, new(scenario, operation, timer.Elapsed.TotalMilliseconds, false, null, false, true, ex.GetType().Name)); }
    catch (Exception ex) { timer.Stop(); Perf.Record(samples, new(scenario, operation, timer.Elapsed.TotalMilliseconds, false, null, false, false, ex.GetType().Name)); }
}

static IReadOnlyList<Aggregate> Aggregate(List<Sample> samples) => samples
    .GroupBy(x => new { x.Scenario, x.Operation }).OrderBy(x => x.Key.Scenario).ThenBy(x => x.Key.Operation)
    .Select(group =>
    {
        double[] values = group.Select(x => x.DurationMs).Order().ToArray();
        return new Aggregate(group.Key.Scenario, group.Key.Operation, values.Length, values.Sum() / 1000d,
            Percentile(values, .50), Percentile(values, .95), Percentile(values, .99), values[^1],
            group.Count(x => !x.Success), group.Count(x => x.Http429), group.Count(x => x.Timeout),
            group.Count(x => !x.Success) * 100d / values.Length);
    }).ToArray();

static double Percentile(double[] sorted, double percentile)
{
    if (sorted.Length == 1) return sorted[0];
    double position = (sorted.Length - 1) * percentile;
    int lower = (int)Math.Floor(position), upper = (int)Math.Ceiling(position);
    return sorted[lower] + (sorted[upper] - sorted[lower]) * (position - lower);
}

static async Task<DatabaseValidation> ValidateDatabaseAsync(string cs)
{
    await using NpgsqlConnection connection = new(cs); await connection.OpenAsync();
    async Task<long> Scalar(string sql) { await using NpgsqlCommand cmd = new(sql, connection); return Convert.ToInt64(await cmd.ExecuteScalarAsync()); }
    return new(await Scalar("SELECT count(*) FROM matches WHERE status='active'"),
        await Scalar("SELECT count(*) FROM pending_round_plans"),
        await Scalar("SELECT count(*) FROM match_commands WHERE outcome IS NULL"),
        await Scalar("SELECT count(*) FROM (SELECT match_id, sequence_number, count(*) FROM match_events GROUP BY match_id, sequence_number HAVING count(*) > 1) duplicates"),
        await Scalar("SELECT count(*) FROM matches"), await Scalar("SELECT count(*) FROM match_events"));
}

sealed class MatchClient : IAsyncDisposable
{
    private readonly PerformanceApplication app;
    private HttpClient alice = null!, bob = null!;
    private HubConnection hubA = null!, hubB = null!;
    private Channel<JsonElement> roundsA = null!, roundsB = null!, statesA = null!, statesB = null!;
    private long sequence;
    private int energyA = 1, energyB = 1;
    public Guid MatchId { get; private set; }
    public string AliceSubject { get; private set; } = "";
    public bool Active { get; private set; } = true;
    private MatchClient(PerformanceApplication app) => this.app = app;

    public static async Task<MatchClient> CreateAsync(PerformanceApplication app, string id, List<Sample>? samples)
    {
        MatchClient match = new(app); await match.InitializeAsync(id, samples); return match;
    }

    private async Task InitializeAsync(string id, List<Sample>? samples)
    {
        const string scenarioFallback = "warmup";
        string scenario = samples is null ? scenarioFallback : id.Split('-', 2)[0] switch { "A" => "A-baseline", "B" => "B-moderate", "C" => "C-high", "reconnect" => "E-reconnect", _ => id };
        AliceSubject = $"perf|{id}|alice"; string bobSubject = $"perf|{id}|bob";
        alice = app.Player(AliceSubject); bob = app.Player(bobSubject);
        CreatedRoomResponse? created = null;
        await Perf.TimedAsync(samples, scenario, "http.room.create", async () =>
        { using var response = await alice.PostAsJsonAsync("/api/rooms", new { visibility = "private" }); response.EnsureSuccessStatusCode(); created = await response.Content.ReadFromJsonAsync<CreatedRoomResponse>(); });
        await Perf.TimedAsync(samples, scenario, "http.room.join", async () =>
        { using var response = await bob.PostAsJsonAsync($"/api/rooms/{created!.Room.RoomId}/join", new { inviteCode = created.InviteCode }); response.EnsureSuccessStatusCode(); });
        await Perf.TimedAsync(samples, scenario, "http.room.get", async () =>
        { using var response = await alice.GetAsync($"/api/rooms/{created!.Room.RoomId}"); response.EnsureSuccessStatusCode(); });
        hubA = app.Hub(AliceSubject); hubB = app.Hub(bobSubject); AttachChannels();
        await Perf.TimedAsync(samples, scenario, "signalr.connect", () => Task.WhenAll(hubA.StartAsync(), hubB.StartAsync()));
        (await alice.PutAsJsonAsync($"/api/rooms/{created!.Room.RoomId}/ready", new { isReady = true })).EnsureSuccessStatusCode();
        await Perf.TimedAsync(samples, scenario, "http.match.start", async () =>
        { using var response = await bob.PutAsJsonAsync($"/api/rooms/{created.Room.RoomId}/ready", new { isReady = true }); response.EnsureSuccessStatusCode(); MatchId = (await response.Content.ReadFromJsonAsync<RoomResponse>())!.MatchId!.Value; });
        object join = new { type = "match.join", version = 1, matchId = MatchId };
        await Task.WhenAll(hubA.InvokeAsync("JoinMatch", join), hubB.InvokeAsync("JoinMatch", join));
        await Task.WhenAll(
            hubA.InvokeAsync("SetMatchNickname", new { type = "match.nickname.set", version = 1, requestId = Guid.NewGuid(), matchId = MatchId, nickname = "Alice" }),
            hubB.InvokeAsync("SetMatchNickname", new { type = "match.nickname.set", version = 1, requestId = Guid.NewGuid(), matchId = MatchId, nickname = "Bob" }));
        JsonElement stateA = await ActiveAsync(statesA); JsonElement stateB = await ActiveAsync(statesB);
        sequence = stateA.GetProperty("sequence").GetInt64(); energyA = stateA.GetProperty("you").GetProperty("energy").GetInt32(); energyB = stateB.GetProperty("you").GetProperty("energy").GetInt32();
    }

    private void AttachChannels()
    {
        roundsA = Channel.CreateUnbounded<JsonElement>(); roundsB = Channel.CreateUnbounded<JsonElement>();
        statesA = Channel.CreateUnbounded<JsonElement>(); statesB = Channel.CreateUnbounded<JsonElement>();
        hubA.On<JsonElement>("round.resolved", value => roundsA.Writer.TryWrite(value)); hubB.On<JsonElement>("round.resolved", value => roundsB.Writer.TryWrite(value));
        hubA.On<JsonElement>("match.state.snapshot", value => statesA.Writer.TryWrite(value)); hubB.On<JsonElement>("match.state.snapshot", value => statesB.Writer.TryWrite(value));
    }

    public async Task ResolveRoundAsync(List<Sample>? samples)
    {
        if (!Active) return;
        string scenario = samples is null ? "warmup" : AliceSubject.Split('|')[1].Split('-', 2)[0] switch { "A" => "A-baseline", "B" => "B-moderate", "C" => "C-high", _ => "unknown" };
        object a = new { type = "round.plan.submit", version = 1, requestId = Guid.NewGuid(), matchId = MatchId, expectedSequence = sequence, actions = Enumerable.Repeat("Attack", energyA).ToArray() };
        object b = new { type = "round.plan.submit", version = 1, requestId = Guid.NewGuid(), matchId = MatchId, expectedSequence = sequence, actions = Enumerable.Repeat("Attack", energyB).ToArray() };
        Stopwatch timer = Stopwatch.StartNew();
        try
        {
            await Task.WhenAll(hubA.InvokeAsync("SubmitRoundPlan", a), hubB.InvokeAsync("SubmitRoundPlan", b));
            JsonElement resultA = await NextAsync(roundsA); JsonElement resultB = await NextAsync(roundsB); timer.Stop();
            long seqA = resultA.GetProperty("sequence").GetInt64(), seqB = resultB.GetProperty("sequence").GetInt64();
            bool exact = seqA == sequence + 1 && seqB == seqA;
            Perf.Record(samples, new(scenario, "signalr.round.all-clients", timer.Elapsed.TotalMilliseconds, exact, null, false, false, exact ? null : "sequence_mismatch"));
            sequence = seqA; Active = resultA.GetProperty("status").GetString() == "active";
            if (Active) { energyA = resultA.GetProperty("you").GetProperty("energy").GetInt32(); energyB = resultB.GetProperty("you").GetProperty("energy").GetInt32(); }
        }
        catch (Exception ex) { timer.Stop(); Perf.Record(samples, new(scenario, "signalr.round.all-clients", timer.Elapsed.TotalMilliseconds, false, null, false, ex is TimeoutException, ex.GetType().Name)); Active = false; }
    }

    public async Task ReconnectAliceAsync(List<Sample> samples)
    {
        const string scenario = "E-reconnect";
        await hubA.DisposeAsync();
        hubA = app.Hub(AliceSubject); roundsA = Channel.CreateUnbounded<JsonElement>(); statesA = Channel.CreateUnbounded<JsonElement>();
        hubA.On<JsonElement>("round.resolved", value => roundsA.Writer.TryWrite(value)); hubA.On<JsonElement>("match.state.snapshot", value => statesA.Writer.TryWrite(value));
        await Perf.TimedAsync(samples, scenario, "signalr.reconnect.transport", () => hubA.StartAsync());
        Stopwatch timer = Stopwatch.StartNew();
        await hubA.InvokeAsync("ResumeMatch", new { type = "match.resume", version = 1, matchId = (Guid?)MatchId, lastSequence = (long?)sequence });
        JsonElement state = await NextAsync(statesA); timer.Stop();
        bool valid = state.GetProperty("matchId").GetGuid() == MatchId && state.GetProperty("sequence").GetInt64() == sequence;
        Perf.Record(samples, new(scenario, "signalr.resume.snapshot", timer.Elapsed.TotalMilliseconds, valid, null, false, false, valid ? null : "state_mismatch"));
    }

    public async Task AbandonAsync(List<Sample>? samples)
    {
        if (!Active) return;
        string scenario = samples is null ? "warmup" : AliceSubject.Contains("reconnect") ? "E-reconnect" : AliceSubject.Split('|')[1].Split('-', 2)[0] switch { "A" => "A-baseline", "B" => "B-moderate", "C" => "C-high", _ => "unknown" };
        await Perf.TimedAsync(samples, scenario, "signalr.match.abandon", () => hubB.InvokeAsync("AbandonMatch", MatchId)); Active = false;
    }

    public async ValueTask DisposeAsync()
    {
        if (hubA is not null) await hubA.DisposeAsync(); if (hubB is not null) await hubB.DisposeAsync(); alice?.Dispose(); bob?.Dispose();
    }
    private static async Task<JsonElement> NextAsync(Channel<JsonElement> channel) => await channel.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15));
    private static async Task<JsonElement> ActiveAsync(Channel<JsonElement> channel) { JsonElement message; do message = await NextAsync(channel); while (message.GetProperty("status").GetString() != "active"); return message; }
}

static class Perf
{
    public static void Record(List<Sample>? samples, Sample sample)
    {
        if (samples is null) return;
        lock (samples) samples.Add(sample);
    }
    public static async Task TimedAsync(List<Sample>? samples, string scenario, string operation, Func<Task> action)
    {
        Stopwatch timer = Stopwatch.StartNew();
        try { await action(); timer.Stop(); Record(samples, new(scenario, operation, timer.Elapsed.TotalMilliseconds, true, null, false, false, null)); }
        catch (Exception ex) { timer.Stop(); Record(samples, new(scenario, operation, timer.Elapsed.TotalMilliseconds, false, null, false, ex is TimeoutException, ex.GetType().Name)); throw; }
    }
}

sealed class PerformanceApplication(string connectionString, string issuer, string audience) : WebApplicationFactory<server::Program>
{
    private readonly SymmetricSecurityKey key = new(RandomNumberGenerator.GetBytes(64));
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseContentRoot(Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "src", "Game.Server")));
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:GameDatabase", connectionString); builder.UseSetting("Cors:AllowedOrigins:0", "http://localhost:5173");
        builder.UseSetting("Authentication:Authority", issuer); builder.UseSetting("Authentication:Audience", audience);
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        { ["ConnectionStrings:GameDatabase"] = connectionString, ["Cors:AllowedOrigins:0"] = "http://localhost:5173", ["Authentication:Authority"] = issuer, ["Authentication:Audience"] = audience }));
        builder.ConfigureLogging(logging => logging.ClearProviders());
        builder.ConfigureTestServices(services => services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
        {
            OpenIdConnectConfiguration configuration = new() { Issuer = issuer }; configuration.SigningKeys.Add(key);
            options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
            options.TokenValidationParameters = new() { ValidateIssuer = true, ValidIssuer = issuer, ValidateAudience = true, ValidAudience = audience,
                ValidateLifetime = true, RequireSignedTokens = true, ValidateIssuerSigningKey = true, IssuerSigningKey = key, ValidAlgorithms = [SecurityAlgorithms.HmacSha256] };
        }));
    }
    private string Token(string subject) => new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(issuer, audience, [new Claim("sub", subject)], expires: DateTime.UtcNow.AddMinutes(30), signingCredentials: new(key, SecurityAlgorithms.HmacSha256)));
    public HttpClient Player(string subject) { HttpClient client = CreateClient(); client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token(subject)); return client; }
    public HubConnection Hub(string subject)
    {
        using HttpClient client = CreateClient();
        return new HubConnectionBuilder().WithUrl(new Uri(client.BaseAddress!, "/hubs/game"), options =>
        { options.Transports = HttpTransportType.LongPolling; options.HttpMessageHandlerFactory = _ => Server.CreateHandler(); options.AccessTokenProvider = () => Task.FromResult<string?>(Token(subject)); }).Build();
    }
}

sealed class ResourceProbe : IDisposable
{
    private readonly Process process = Process.GetCurrentProcess(); private readonly TimeSpan cpuStart; private readonly Stopwatch elapsed = Stopwatch.StartNew();
    private readonly CancellationTokenSource stop = new(); private readonly Task monitor; private long peak;
    public string Scenario { get; }
    public ResourceProbe(string scenario) { Scenario = scenario; process.Refresh(); cpuStart = process.TotalProcessorTime; peak = process.WorkingSet64; monitor = Task.Run(async () => { while (!stop.IsCancellationRequested) { process.Refresh(); Interlocked.Exchange(ref peak, Math.Max(Interlocked.Read(ref peak), process.WorkingSet64)); try { await Task.Delay(50, stop.Token); } catch { } } }); }
    public ScenarioResource Finish(int users, int matches, TimeSpan wall) { stop.Cancel(); monitor.GetAwaiter().GetResult(); process.Refresh(); double cpu = (process.TotalProcessorTime - cpuStart).TotalMilliseconds / (elapsed.Elapsed.TotalMilliseconds * Environment.ProcessorCount) * 100; return new(Scenario, users, matches, wall.TotalSeconds, cpu, peak / 1024d / 1024d, process.WorkingSet64 / 1024d / 1024d); }
    public void Dispose() { if (!stop.IsCancellationRequested) stop.Cancel(); }
}

sealed class PerformanceRun
{
    public string Commit { get; set; } = "9a2883e6c1e5518c19ff7760f258ce367632adce";
    public DateTimeOffset StartedAtUtc { get; set; } = DateTimeOffset.UtcNow; public DateTimeOffset FinishedAtUtc { get; set; }
    public bool WarmupCompleted { get; set; } public EnvironmentInfo? Environment { get; set; }
    public List<Sample> Samples { get; } = []; public IReadOnlyList<Aggregate> Aggregates { get; set; } = [];
    public List<ScenarioResource> ScenarioResources { get; } = []; public List<CompletedMatch> CompletedMatches { get; } = [];
    public List<string> SkippedScenarios { get; } = []; public DatabaseValidation Database { get; set; } = new(0,0,0,0,0,0); public int RemainingConnections { get; set; }
}
sealed record Sample(string Scenario, string Operation, double DurationMs, bool Success, int? HttpStatus, bool Http429, bool Timeout, string? Error);
sealed record Aggregate(string Scenario, string Operation, int Count, double SumSeconds, double P50Ms, double P95Ms, double P99Ms, double MaxMs, int Errors, int Http429, int Timeouts, double ErrorRatePercent);
sealed record ScenarioResource(string Scenario, int SyntheticUsers, int ConcurrentMatches, double WallSeconds, double ApproximateCpuPercent, double PeakWorkingSetMiB, double FinalWorkingSetMiB);
sealed record EnvironmentInfo(string OperatingSystem, string DotnetRuntime, int LogicalProcessors, double GcAvailableMemoryMiB, string Process, string ServerTransport, string Database);
sealed record DatabaseValidation(long ActiveMatches, long PendingPlans, long CommandsWithoutOutcome, long DuplicateEventSequences, long TotalMatches, long TotalEvents);
sealed record CompletedMatch(Guid MatchId, string Subject);
