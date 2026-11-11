using System.Collections.Concurrent;
using System.Text.Json;
using Game.Domain;
using Game.Infrastructure;
using Game.Server.Contracts.V1;
using Game.Server.Hubs;
using Game.Server.Rooms;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;

namespace Game.Postgres.Tests;

internal sealed record SentMessage(string Connection, string Event, JsonElement Message);
internal sealed class RecordingGameHub : IHubContext<GameHub>
{
    public ConcurrentQueue<SentMessage> Messages { get; } = new();
    public Func<SentMessage, Task>? OnSend { get; set; }
    public IHubClients Clients => new RecordingClients(this);
    public IGroupManager Groups => throw new NotSupportedException();
    private sealed class Proxy(RecordingGameHub hub, string connection) : IClientProxy
    {
        public async Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
        {
            var message = new SentMessage(connection, method, JsonSerializer.SerializeToElement(args[0]));
            if (hub.OnSend is not null) await hub.OnSend(message);
            hub.Messages.Enqueue(message);
        }
    }
    private sealed class RecordingClients(RecordingGameHub hub) : IHubClients
    {
        public IClientProxy Client(string id) => new Proxy(hub, id);
        public IClientProxy Group(string id) => new Proxy(hub, "group:" + id);
        public IClientProxy All => throw new NotSupportedException();
        public IClientProxy AllExcept(IReadOnlyList<string> ids) => throw new NotSupportedException();
        public IClientProxy Clients(IReadOnlyList<string> ids) => throw new NotSupportedException();
        public IClientProxy GroupExcept(string name, IReadOnlyList<string> ids) => throw new NotSupportedException();
        public IClientProxy Groups(IReadOnlyList<string> names) => throw new NotSupportedException();
        public IClientProxy User(string id) => throw new NotSupportedException();
        public IClientProxy Users(IReadOnlyList<string> ids) => throw new NotSupportedException();
    }
}

internal sealed class CountingRolls(int roll = 1000) : ICriticalHitSource
{
    private int calls;
    public int Calls => Volatile.Read(ref calls);
    public int NextBasisPointRoll() { Interlocked.Increment(ref calls); return roll; }
}

internal sealed class LiveMatch(TestDatabase database, string suffix = "", ICriticalHitSource? rolls = null)
{
    public string Alice { get; } = "auth0|alice" + suffix;
    public string Bob { get; } = "auth0|bob" + suffix;
    public TestDatabase Database { get; } = database;
    public RecordingGameHub Hub { get; } = new();
    public MatchState Initial { get; } = MatchState.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Now());
    public PostgresMatchSessionStore Store => new(Database.Source, NullLogger<PostgresMatchSessionStore>.Instance);
    private static DateTimeOffset Now() { long ticks = DateTimeOffset.UtcNow.UtcTicks; return new(ticks - ticks % 10, TimeSpan.Zero); }

    public MatchRoundService NewService(ICriticalHitSource? critical = null, PostgresMatchSessionStore? store = null) =>
        new(Hub, critical ?? rolls ?? new CountingRolls(), store ?? Store);

    public async Task<MatchRoundService> CreateAsync(bool ready = true)
    {
        var service = NewService();
        await service.CreateAsync(Initial, new Dictionary<string, Guid> { [Alice] = Initial.FirstPlayer.PlayerId, [Bob] = Initial.SecondPlayer.PlayerId });
        if (ready) await ReadyAsync(service);
        return service;
    }
    public async Task ReadyAsync(MatchRoundService service)
    {
        await service.SetNicknameAsync(Initial.MatchId, Alice, "a", "Alice");
        await service.SetNicknameAsync(Initial.MatchId, Bob, "b", "Bob");
        Hub.Messages.Clear();
    }
    public SubmitRoundPlanMessage Command(long sequence = 0, string[]? actions = null, Guid? request = null) =>
        new("round.plan.submit", 1, request ?? Guid.NewGuid(), Initial.MatchId, sequence, actions ?? ["Attack"]);
    public async Task<JsonElement> SnapshotAsync(MatchRoundService service, string? subject = null) =>
        JsonSerializer.SerializeToElement(await service.AttachAsync(Initial.MatchId, subject ?? Alice, "read"));
}
