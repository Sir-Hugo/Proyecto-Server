using System.Text.Json;
using Game.Domain;
using Game.Infrastructure;
using Game.Server.Contracts.V1;
using Game.Server.Hubs;
using Game.Server.Rooms;
using Microsoft.AspNetCore.SignalR;

namespace Game.Server.Tests;

public sealed class MatchRoundServiceTests
{
    private sealed class FixedRoll : ICriticalHitSource { public int NextBasisPointRoll() => 1000; }
    private sealed record Sent(string Connection, string Event, JsonElement Payload);
    private sealed class RecordingHub : IHubContext<GameHub>
    {
        public List<Sent> Messages { get; } = [];
        public IHubClients Clients => new RecordingClients(Messages);
        public IGroupManager Groups => throw new NotSupportedException();
    }
    private sealed class RecordingProxy(List<Sent> messages, string connection) : IClientProxy
    {
        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
        { messages.Add(new(connection, method, JsonSerializer.SerializeToElement(args[0]))); return Task.CompletedTask; }
    }
    private sealed class RecordingClients(List<Sent> messages) : IHubClients
    {
        public IClientProxy Client(string connectionId) => new RecordingProxy(messages, connectionId);
        public IClientProxy All => throw new NotSupportedException();
        public IClientProxy AllExcept(IReadOnlyList<string> ids) => throw new NotSupportedException();
        public IClientProxy Clients(IReadOnlyList<string> ids) => throw new NotSupportedException();
        public IClientProxy Group(string name) => throw new NotSupportedException();
        public IClientProxy GroupExcept(string name, IReadOnlyList<string> ids) => throw new NotSupportedException();
        public IClientProxy Groups(IReadOnlyList<string> names) => throw new NotSupportedException();
        public IClientProxy User(string id) => throw new NotSupportedException();
        public IClientProxy Users(IReadOnlyList<string> ids) => throw new NotSupportedException();
    }
    private sealed class Fixture
    {
        public RecordingHub Hub { get; } = new();
        public MatchState Match { get; }
        public MatchRoundService Service { get; }
        public Fixture(MatchState? match = null)
        {
            Match = match ?? MatchState.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow);
            var store = new InMemoryMatchSessionStore();
            store.Seed(new(Match, new Dictionary<string, Guid> { ["alice"] = Match.FirstPlayer.PlayerId, ["bob"] = Match.SecondPlayer.PlayerId }));
            Service = new(Hub, new FixedRoll(), store);
        }
        public async Task Ready()
        { await Service.SetNicknameAsync(Match.MatchId, "alice", "a", "Alice"); await Service.SetNicknameAsync(Match.MatchId, "bob", "b", "Bob"); Hub.Messages.Clear(); }
        public SubmitRoundPlanMessage Command(long sequence = 0, string action = "Attack") => new("round.plan.submit", 1, Guid.NewGuid(), Match.MatchId, sequence, [action]);
        public JsonElement Snapshot(string subject = "alice") => JsonSerializer.SerializeToElement(Service.AttachAsync(Match.MatchId, subject, subject == "alice" ? "a" : "b").GetAwaiter().GetResult());
    }
    [Fact] public async Task RejectsStaleAndFutureSequencesWithoutChangingState()
    {
        var f = new Fixture(); await f.Ready();
        Assert.Equal("stale_sequence", (await Assert.ThrowsAsync<HubException>(() => f.Service.SubmitAsync("alice", "a", f.Command(1)))).Message);
        await f.Service.SubmitAsync("alice", "a", f.Command()); await f.Service.SubmitAsync("bob", "b", f.Command());
        Assert.Equal("stale_sequence", (await Assert.ThrowsAsync<HubException>(() => f.Service.SubmitAsync("alice", "a", f.Command()))).Message);
        Assert.Equal(1, f.Snapshot().GetProperty("sequence").GetInt64());
    }
    [Fact] public async Task IdenticalRetryReplaysAcknowledgementAndOutcomeWithoutApplyingDamageAgain()
    {
        var f = new Fixture(); await f.Ready(); var command = f.Command();
        await f.Service.SubmitAsync("alice", "a", command);
        var ack = f.Hub.Messages.Single().Payload.GetRawText();
        await f.Service.SubmitAsync("alice", "a", command);
        Assert.Equal(ack, f.Hub.Messages.Last().Payload.GetRawText());
        await f.Service.SubmitAsync("bob", "b", f.Command());
        var outcome = f.Hub.Messages.Single(m => m.Connection == "a" && m.Event == "round.resolved").Payload.GetRawText();
        f.Hub.Messages.Clear();
        await f.Service.SubmitAsync("alice", "retry", command);
        Assert.Equal(2, f.Hub.Messages.Count);
        Assert.All(f.Hub.Messages, m => Assert.Equal("retry", m.Connection));
        Assert.Equal(ack, f.Hub.Messages[0].Payload.GetRawText()); Assert.Equal(outcome, f.Hub.Messages[1].Payload.GetRawText());
        Assert.Equal(1, f.Snapshot().GetProperty("sequence").GetInt64()); Assert.Equal(45, f.Snapshot().GetProperty("you").GetProperty("health").GetInt32());
    }
    [Fact] public async Task ChangedContentCannotReuseRequestId()
    {
        var f = new Fixture(); await f.Ready(); var command = f.Command(); await f.Service.SubmitAsync("alice", "a", command);
        foreach (var changed in new[] { command with { Actions = ["Reserve"] }, command with { ExpectedSequence = 1 } })
            Assert.Equal("idempotency_key_reused", (await Assert.ThrowsAsync<HubException>(() => f.Service.SubmitAsync("alice", "a", changed))).Message);
        Assert.Equal(0, f.Snapshot().GetProperty("sequence").GetInt64());
    }
    [Fact] public async Task RequestIdsAreScopedToParticipant()
    {
        var f = new Fixture(); await f.Ready(); var command = f.Command();
        await f.Service.SubmitAsync("alice", "a", command); await f.Service.SubmitAsync("bob", "b", command with { Actions = ["Reserve"] });
        Assert.Equal(1, f.Snapshot().GetProperty("sequence").GetInt64());
    }
    [Fact] public async Task SecondPlanFromSamePlayerRejected()
    {
        var f = new Fixture(); await f.Ready(); await f.Service.SubmitAsync("alice", "a", f.Command());
        Assert.Equal("round_plan_already_submitted", (await Assert.ThrowsAsync<HubException>(() => f.Service.SubmitAsync("alice", "a", f.Command()))).Message);
    }
    [Fact] public async Task RequiresNicknamesAndValidDomainPlan()
    {
        var f = new Fixture(); Assert.Equal("nicknames_required", (await Assert.ThrowsAsync<HubException>(() => f.Service.SubmitAsync("alice", "a", f.Command()))).Message);
        await f.Ready(); var ex = await Assert.ThrowsAsync<HubException>(() => f.Service.SubmitAsync("alice", "a", f.Command() with { Actions = ["Attack", "Attack"] }));
        Assert.StartsWith("invalid_round_plan:", ex.Message); Assert.Empty(f.Hub.Messages);
        await f.Service.SubmitAsync("alice", "a", f.Command());
    }
    [Fact] public async Task NonParticipantsCannotReadOrAct()
    {
        var f = new Fixture();
        Assert.Equal("not_match_participant", Assert.Throws<HubException>(() => f.Service.AttachAsync(f.Match.MatchId, "outsider", "x").GetAwaiter().GetResult()).Message);
        Assert.Equal("not_match_participant", (await Assert.ThrowsAsync<HubException>(() => f.Service.SubmitAsync("outsider", "x", f.Command()))).Message);
        await Assert.ThrowsAsync<HubException>(() => f.Service.AbandonAsync(f.Match.MatchId, "outsider"));
        await Assert.ThrowsAsync<HubException>(() => f.Service.SetNicknameAsync(f.Match.MatchId, "outsider", "x", "Spy"));
        Assert.Empty(f.Hub.Messages);
    }
    [Fact] public async Task SnapshotsExposeOnlyOwnPrivateState()
    {
        var f = new Fixture(); await f.Ready(); await f.Service.SubmitAsync("alice", "a", f.Command());
        foreach (var subject in new[] { "alice", "bob" })
        {
            var snapshot = f.Snapshot(subject); var you = snapshot.GetProperty("you"); var opponent = snapshot.GetProperty("opponent");
            Assert.Equal(subject == "alice" ? f.Match.FirstPlayer.PlayerId : f.Match.SecondPlayer.PlayerId, you.GetProperty("playerId").GetGuid());
            Assert.Equal(1, you.GetProperty("energy").GetInt32());
            Assert.Equal(new[] { "health", "isHost", "nickname", "playerId" }, opponent.EnumerateObject().Select(p => p.Name).Order().ToArray());
            Assert.False(snapshot.TryGetProperty("actions", out _)); Assert.Null(snapshot.GetProperty("winnerId").GetString());
        }
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public void FinishedSnapshotPreservesWinnerOrDraw(bool draw)
    {
        var m = MatchState.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow);
        m = m with { Status = MatchStatus.Finished, WinnerId = draw ? null : m.FirstPlayer.PlayerId };
        var f = new Fixture(m);
        foreach (var subject in new[] { "alice", "bob" })
        { var s = f.Snapshot(subject); Assert.Equal("completed", s.GetProperty("status").GetString()); Assert.Equal(m.WinnerId, s.GetProperty("winnerId").Deserialize<Guid?>()); }
    }
    [Fact] public async Task AbandonmentCanBeReconstructedAndFurtherActionsRejected()
    {
        var f = new Fixture(); await f.Ready(); await f.Service.AbandonAsync(f.Match.MatchId, "alice");
        Assert.Equal(f.Match.SecondPlayer.PlayerId, f.Snapshot().GetProperty("winnerId").GetGuid());
        Assert.Equal("completed", f.Snapshot().GetProperty("status").GetString());
        Assert.Equal("match_completed", (await Assert.ThrowsAsync<HubException>(() => f.Service.SubmitAsync("alice", "a", f.Command()))).Message);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task InactivityProducesDrawOrAwardsActivePlayer(bool submitted)
    {
        var f = new Fixture(); await f.Ready(); if (submitted) await f.Service.SubmitAsync("alice", "a", f.Command());
        Assert.Equal(2, await f.Service.ExpireInactiveMatchesAsync(f.Match.LastActivityAt.AddMinutes(6), TimeSpan.FromMinutes(5), CancellationToken.None));
        Assert.Equal(submitted ? f.Match.FirstPlayer.PlayerId : (Guid?)null, f.Snapshot().GetProperty("winnerId").Deserialize<Guid?>());
        Assert.Equal(0, await f.Service.ExpireInactiveMatchesAsync(f.Match.LastActivityAt.AddMinutes(7), TimeSpan.FromMinutes(5), CancellationToken.None));
    }
}
