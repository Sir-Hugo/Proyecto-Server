using System.Text.Json;
using Game.Domain;
using Game.Infrastructure;
using Game.Server.Rooms;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Game.Postgres.Tests;

[Trait("Category", "PostgreSQL")]
public sealed class LiveMatchTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static async Task<string?> ErrorAsync(Func<Task> operation)
    { try { await operation(); return null; } catch (HubException error) { return error.Message; } }

    [PostgresFact] public async Task CreatesDurableParticipantsInitialEventAndLifecycleDates()
    {
        await using var db = await fixture.CreateDatabaseAsync(); var game = new LiveMatch(db);
        var service = await game.CreateAsync(false);
        Assert.Equal(game.Initial, (await db.Store.GetAsync(game.Initial.MatchId))!.State);
        Assert.Equal(2L, await db.ScalarAsync<long>("SELECT count(*) FROM match_participants"));
        Assert.Equal(2L, await db.ScalarAsync<long>("SELECT count(*) FROM active_match_players"));
        Assert.Equal("match.started", await db.ScalarAsync<string>("SELECT event_type FROM match_events"));
        Assert.True(await db.ScalarAsync<bool>("SELECT started_at IS NULL AND completed_at IS NULL FROM matches"));
        await game.ReadyAsync(service);
        Assert.True(await db.ScalarAsync<bool>("SELECT started_at >= created_at AND completed_at IS NULL FROM matches"));
    }
    [PostgresFact] public async Task FirstPlanIsDurableAndDoesNotAdvanceSequence()
    {
        await using var db = await fixture.CreateDatabaseAsync(); var game = new LiveMatch(db); var service = await game.CreateAsync();
        var command = game.Command(); await service.SubmitAsync(game.Alice, "a", command);
        var pending = Assert.IsType<StoredPendingPlan>(await db.Store.GetPendingPlanAsync(game.Initial.MatchId, game.Initial.FirstPlayer.PlayerId));
        Assert.Equal(command.RequestId, pending.RequestId); Assert.Equal(new[] { GameAction.Attack }, pending.Actions);
        Assert.Equal(0, (await db.Store.GetAsync(game.Initial.MatchId))!.State.SequenceNumber);
        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT count(*) FROM match_commands"));
        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT count(*) FROM match_events"));
    }
    [PostgresFact] public async Task SecondPlanResolvesExactlyOnceAndCachesBothPrivateResults()
    {
        await using var db = await fixture.CreateDatabaseAsync(); var rolls = new CountingRolls(); var game = new LiveMatch(db, rolls: rolls); var service = await game.CreateAsync();
        var a = game.Command(); var b = game.Command();
        await service.SubmitAsync(game.Alice, "a", a); await service.SubmitAsync(game.Bob, "b", b);
        for (int i = 0; i < 3; i++) { await service.SubmitAsync(game.Alice, "a", a); await service.SubmitAsync(game.Bob, "b", b); }
        var state = (await db.Store.GetAsync(game.Initial.MatchId))!.State;
        Assert.Equal(1, state.SequenceNumber); Assert.Equal(2, state.RoundNumber); Assert.Equal(45, state.FirstPlayer.Health);
        Assert.Equal(2, rolls.Calls); Assert.Equal(0L, await db.ScalarAsync<long>("SELECT count(*) FROM pending_round_plans"));
        Assert.Equal(2L, await db.ScalarAsync<long>("SELECT count(*) FROM match_commands WHERE outcome IS NOT NULL AND outcome_event_type='round.resolved'"));
        Assert.Equal(new long[] { 0, 1 }, await SequencesAsync(db));
    }
    [PostgresFact] public async Task IdenticalRetrySurvivesFreshPoolAndService()
    {
        await using var db = await fixture.CreateDatabaseAsync(); var game = new LiveMatch(db); var service = await game.CreateAsync();
        var command = game.Command(); await service.SubmitAsync(game.Alice, "a", command); await service.SubmitAsync(game.Bob, "b", game.Command());
        var original = game.Hub.Messages.Last(message => message.Connection == "a" && message.Event == "round.resolved").Message;
        await using var source = PostgresServices.CreateDataSource(db.ConnectionString);
        var restarted = game.NewService(new CountingRolls(0), new(source, NullLogger<PostgresMatchSessionStore>.Instance));
        game.Hub.Messages.Clear(); await restarted.SubmitAsync(game.Alice, "new", command);
        var messages = game.Hub.Messages.ToArray(); Assert.Equal(2, messages.Length);
        Assert.True(JsonElement.DeepEquals(original, messages[1].Message)); Assert.Equal("new", messages[1].Connection);
        Assert.Equal(1, (await db.Store.GetAsync(game.Initial.MatchId))!.State.SequenceNumber);
    }
    [PostgresTheory] [InlineData(false)] [InlineData(true)]
    public async Task SameRequestWithChangedContentIsRejectedAfterRestart(bool sequence)
    {
        await using var db = await fixture.CreateDatabaseAsync(); var game = new LiveMatch(db); var service = await game.CreateAsync();
        var command = game.Command(); await service.SubmitAsync(game.Alice, "a", command);
        var changed = sequence ? command with { ExpectedSequence = 1 } : command with { Actions = ["Reserve"] };
        Assert.Equal("idempotency_key_reused", await ErrorAsync(() => game.NewService().SubmitAsync(game.Alice, "new", changed)));
        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT count(*) FROM match_commands"));
    }
    [PostgresFact] public async Task SimultaneousPlayersAcrossServiceInstancesResolveOnce()
    {
        await using var db = await fixture.CreateDatabaseAsync(); var rolls = new CountingRolls(); var game = new LiveMatch(db, rolls: rolls); await game.CreateAsync();
        await Task.WhenAll(game.NewService().SubmitAsync(game.Alice, "a", game.Command()), game.NewService().SubmitAsync(game.Bob, "b", game.Command()));
        Assert.Equal(1, (await db.Store.GetAsync(game.Initial.MatchId))!.State.SequenceNumber);
        Assert.Equal(2, rolls.Calls); Assert.Equal(1L, await db.ScalarAsync<long>("SELECT count(*) FROM match_events WHERE event_type='round.resolved'"));
    }
    [PostgresFact] public async Task SimultaneousDistinctRequestsFromSamePlayerAcceptOnlyOne()
    {
        await using var db = await fixture.CreateDatabaseAsync(); var game = new LiveMatch(db); await game.CreateAsync();
        var errors = await Task.WhenAll(ErrorAsync(() => game.NewService().SubmitAsync(game.Alice, "a", game.Command())),
            ErrorAsync(() => game.NewService().SubmitAsync(game.Alice, "other", game.Command())));
        Assert.Single(errors, error => error is null); Assert.Contains("round_plan_already_submitted", errors);
        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT count(*) FROM match_commands"));
        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT count(*) FROM pending_round_plans"));
    }
    [PostgresFact] public async Task SimultaneousIdenticalRequestIsPersistedOnce()
    {
        await using var db = await fixture.CreateDatabaseAsync(); var game = new LiveMatch(db); await game.CreateAsync(); var command = game.Command();
        await Task.WhenAll(game.NewService().SubmitAsync(game.Alice, "a", command), game.NewService().SubmitAsync(game.Alice, "other", command));
        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT count(*) FROM match_commands"));
        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT count(*) FROM pending_round_plans"));
    }
    [PostgresFact] public async Task ConcurrentReuseOfIdWithDifferentActionsHasOneWinner()
    {
        await using var db = await fixture.CreateDatabaseAsync(); var game = new LiveMatch(db); await game.CreateAsync(); var command = game.Command();
        var errors = await Task.WhenAll(ErrorAsync(() => game.NewService().SubmitAsync(game.Alice, "a", command)),
            ErrorAsync(() => game.NewService().SubmitAsync(game.Alice, "b", command with { Actions = ["Reserve"] })));
        Assert.Single(errors, error => error is null); Assert.Contains("idempotency_key_reused", errors);
    }
    [PostgresFact] public async Task LockedMatchDoesNotBlockAnotherMatch()
    {
        await using var db = await fixture.CreateDatabaseAsync(); var first = new LiveMatch(db, "-1"); var second = new LiveMatch(db, "-2");
        var service1 = await first.CreateAsync(); var service2 = await second.CreateAsync();
        await using var connection = await db.Source.OpenConnectionAsync(); await using var transaction = await connection.BeginTransactionAsync();
        await using (var locked = new NpgsqlCommand("SELECT id FROM matches WHERE id=$1 FOR UPDATE", connection, transaction))
        { locked.Parameters.AddWithValue(first.Initial.MatchId); await locked.ExecuteScalarAsync(); }
        Task blocked = service1.SubmitAsync(first.Alice, "a", first.Command());
        try
        {
            await Task.WhenAll(service2.SubmitAsync(second.Alice, "a2", second.Command()), service2.SubmitAsync(second.Bob, "b2", second.Command())).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(blocked.IsCompleted); Assert.Equal(1, (await db.Store.GetAsync(second.Initial.MatchId))!.State.SequenceNumber);
        }
        finally { await transaction.RollbackAsync(); await blocked; }
    }
    [PostgresFact] public async Task EventInsertFailureRollsBackStateCommandsAndPendingPlans()
    {
        await using var db = await fixture.CreateDatabaseAsync(); var game = new LiveMatch(db); var service = await game.CreateAsync();
        await service.SubmitAsync(game.Alice, "a", game.Command()); var second = game.Command();
        await FailEventsAsync(db); game.Hub.Messages.Clear();
        Assert.Equal("database_unavailable", await ErrorAsync(() => service.SubmitAsync(game.Bob, "b", second)));
        Assert.Empty(game.Hub.Messages); Assert.Equal(0, (await db.Store.GetAsync(game.Initial.MatchId))!.State.SequenceNumber);
        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT count(*) FROM pending_round_plans"));
        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT count(*) FROM match_commands"));
        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT count(*) FROM match_events"));
        await db.ExecuteAsync("DROP TRIGGER injected_event_failure ON match_events");
        await service.SubmitAsync(game.Bob, "b", second);
        Assert.Equal(1, (await db.Store.GetAsync(game.Initial.MatchId))!.State.SequenceNumber);
    }
    [PostgresFact] public async Task UnauthorizedAndUnknownMatchesDoNotWriteAnything()
    {
        await using var db = await fixture.CreateDatabaseAsync(); var game = new LiveMatch(db); var service = await game.CreateAsync();
        Assert.Equal("not_match_participant", await ErrorAsync(() => service.SubmitAsync("outsider", "x", game.Command())));
        Assert.Equal("not_match_participant", await ErrorAsync(() => service.AttachAsync(game.Initial.MatchId, "outsider", "x")));
        Assert.Equal("not_match_participant", await ErrorAsync(() => service.SubmitAsync(game.Alice, "x", game.Command() with { MatchId = Guid.NewGuid() })));
        Assert.Equal(0L, await db.ScalarAsync<long>("SELECT count(*) FROM match_commands"));
    }
    [PostgresTheory] [InlineData("0")] [InlineData("Unknown")] [InlineData("Attack,Attack")] [InlineData("SpecialAttack")]
    public async Task InvalidPlansNeverBecomeConfirmed(string actions)
    {
        await using var db = await fixture.CreateDatabaseAsync(); var game = new LiveMatch(db); var service = await game.CreateAsync();
        Assert.NotNull(await ErrorAsync(() => service.SubmitAsync(game.Alice, "a", game.Command(actions: actions.Split(',')))));
        Assert.Equal(0L, await db.ScalarAsync<long>("SELECT count(*) FROM match_commands")); Assert.Empty(game.Hub.Messages);
    }
    [PostgresFact] public async Task PrivateMessagesNeverContainEitherSubmittedPlan()
    {
        await using var db = await fixture.CreateDatabaseAsync(); var game = new LiveMatch(db); var service = await game.CreateAsync();
        await service.SubmitAsync(game.Alice, "a", game.Command());
        var snapshot = await game.SnapshotAsync(game.NewService(), game.Bob);
        Assert.DoesNotContain("actions", snapshot.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.False(snapshot.GetProperty("opponent").TryGetProperty("energy", out _));
        await service.SubmitAsync(game.Bob, "b", game.Command(actions: ["Reserve"]));
        Assert.All(game.Hub.Messages, message => Assert.DoesNotContain("actions", message.Message.GetRawText(), StringComparison.OrdinalIgnoreCase));
        foreach (var message in game.Hub.Messages.Where(m => m.Event == "round.resolved"))
            Assert.Equal(new[] { "health", "isHost", "nickname", "playerId" }, message.Message.GetProperty("opponent").EnumerateObject().Select(p => p.Name).Order().ToArray());
    }
    [PostgresFact] public async Task PendingPlanSurvivesNewPoolAndCanBeResolved()
    {
        await using var db = await fixture.CreateDatabaseAsync(); var game = new LiveMatch(db); var service = await game.CreateAsync();
        await service.SubmitAsync(game.Alice, "a", game.Command());
        await using var source = PostgresServices.CreateDataSource(db.ConnectionString);
        var store = new PostgresMatchSessionStore(source, NullLogger<PostgresMatchSessionStore>.Instance);
        var reloaded = Assert.IsType<MatchSession>(await store.ReadAsync(game.Initial.MatchId)); Assert.Single(reloaded.PendingPlans);
        await game.NewService(store: store).SubmitAsync(game.Bob, "b", game.Command());
        Assert.Equal(1, (await store.ReadAsync(game.Initial.MatchId))!.Match.SequenceNumber);
    }
    [PostgresFact] public async Task EventReplaysCriticalRoundWithoutDrawingNewRandomness()
    {
        await using var db = await fixture.CreateDatabaseAsync(); var game = new LiveMatch(db, rolls: new CountingRolls(999)); var service = await game.CreateAsync();
        await service.SubmitAsync(game.Alice, "a", game.Command()); await service.SubmitAsync(game.Bob, "b", game.Command());
        var payload = JsonSerializer.Deserialize<JsonElement>(await db.ScalarAsync<string>("SELECT payload::text FROM match_events WHERE sequence_number=1"));
        var before = payload.GetProperty("before").Deserialize<MatchState>()!;
        var firstPlan = payload.GetProperty("firstPlan").Deserialize<RoundPlan>()!;
        var secondPlan = payload.GetProperty("secondPlan").Deserialize<RoundPlan>()!;
        var after = payload.GetProperty("after").Deserialize<MatchState>()!;
        var replay = new ReplayRolls(payload.GetProperty("criticalRolls").Deserialize<int[]>()!);
        var result = new RoundResolver(replay).Resolve(before, firstPlan, secondPlan, after.LastActivityAt);
        Assert.Equal(after, result.Match); Assert.Equal(after, (await db.Store.GetAsync(game.Initial.MatchId))!.State);
        Assert.Equal(15, result.FirstPlayerDamage); Assert.Equal(0, replay.Remaining);
    }
    [PostgresFact] public async Task FinishedMatchRejectsChangesAndReleasesPlayers()
    {
        await using var db = await fixture.CreateDatabaseAsync(); var game = new LiveMatch(db); var service = await game.CreateAsync();
        while ((await db.Store.GetAsync(game.Initial.MatchId))!.State.Status == MatchStatus.Active)
        {
            var state = (await db.Store.GetAsync(game.Initial.MatchId))!.State;
            await service.SubmitAsync(game.Alice, "a", game.Command(state.SequenceNumber, Enumerable.Repeat("Attack", state.FirstPlayer.Energy).ToArray()));
            await service.SubmitAsync(game.Bob, "b", game.Command(state.SequenceNumber, Enumerable.Repeat("Reserve", state.SecondPlayer.Energy).ToArray()));
        }
        var finished = (await db.Store.GetAsync(game.Initial.MatchId))!.State;
        Assert.Equal(game.Initial.FirstPlayer.PlayerId, finished.WinnerId);
        Assert.True(await db.ScalarAsync<bool>("SELECT completed_at >= started_at FROM matches"));
        Assert.Equal("match_completed", await ErrorAsync(() => service.SubmitAsync(game.Alice, "a", game.Command(finished.SequenceNumber))));
        Assert.Equal("match_completed", await ErrorAsync(() => service.AbandonAsync(game.Initial.MatchId, game.Alice)));
        Assert.Equal("23514", (await Assert.ThrowsAsync<PostgresException>(() => db.ExecuteAsync("UPDATE matches SET winner_player_id=second_player_id"))).SqlState);
        Assert.False(await service.IsPlayerActiveAsync(game.Alice));
        var next = new LiveMatch(db); await next.CreateAsync(false);
        Assert.Equal(2L, await db.ScalarAsync<long>("SELECT count(*) FROM matches"));
    }
    [PostgresFact] public async Task AbandonmentPersistsOutcomeEvenWhenPendingPlayerIsDisconnected()
    {
        await using var db = await fixture.CreateDatabaseAsync(); var game = new LiveMatch(db); var service = await game.CreateAsync(); var command = game.Command();
        await service.SubmitAsync(game.Alice, "a", command);
        await game.NewService().AbandonAsync(game.Initial.MatchId, game.Bob);
        game.Hub.Messages.Clear(); await game.NewService().SubmitAsync(game.Alice, "again", command);
        Assert.Equal("match.ended", game.Hub.Messages.Last().Event);
        Assert.Equal(game.Initial.FirstPlayer.PlayerId, (await db.Store.GetAsync(game.Initial.MatchId))!.State.WinnerId);
        Assert.Equal(0L, await db.ScalarAsync<long>("SELECT count(*) FROM pending_round_plans"));
    }
    [PostgresTheory] [InlineData(false)] [InlineData(true)]
    public async Task InactivityUsesPersistedPlansAndFinishesOnlyOnce(bool submitted)
    {
        await using var db = await fixture.CreateDatabaseAsync(); var game = new LiveMatch(db); var service = await game.CreateAsync();
        if (submitted) await service.SubmitAsync(game.Alice, "a", game.Command());
        var fresh = game.NewService();
        await Task.WhenAll(fresh.ExpireInactiveMatchesAsync(game.Initial.LastActivityAt.AddMinutes(6), TimeSpan.FromMinutes(5), CancellationToken.None),
            game.NewService().ExpireInactiveMatchesAsync(game.Initial.LastActivityAt.AddMinutes(6), TimeSpan.FromMinutes(5), CancellationToken.None));
        var state = (await db.Store.GetAsync(game.Initial.MatchId))!.State;
        Assert.Equal(MatchStatus.Finished, state.Status); Assert.Equal(submitted ? game.Initial.FirstPlayer.PlayerId : (Guid?)null, state.WinnerId);
        Assert.Equal(1, state.SequenceNumber); Assert.Equal(new long[] { 0, 1 }, await SequencesAsync(db));
    }
    [PostgresTheory] [InlineData("public")] [InlineData("private")]
    public async Task RoomFlowCreatesDurableMatchOnlyWhenBothPlayersAreReady(string visibility)
    {
        await using var db = await fixture.CreateDatabaseAsync(); var game = new LiveMatch(db); var service = game.NewService(); var rooms = new InMemoryRoomService(service, db.Source);
        var created = rooms.Create(game.Alice, visibility).Value!;
        Assert.True(rooms.Join(created.Room.RoomId, game.Bob, created.InviteCode).IsSuccess);
        Assert.Null((await rooms.SetReadyAsync(created.Room.RoomId, game.Alice, true)).Value!.MatchId);
        Assert.Equal(0L, await db.ScalarAsync<long>("SELECT count(*) FROM matches"));
        var ready = await rooms.SetReadyAsync(created.Room.RoomId, game.Bob, true); Assert.True(ready.IsSuccess);
        Assert.NotNull(await db.Store.GetAsync(ready.Value!.MatchId!.Value));
        Assert.Equal(2L, await db.ScalarAsync<long>("SELECT count(*) FROM match_participants"));
        await service.AbandonAsync(ready.Value.MatchId.Value, game.Bob);
        Assert.Equal("Partida finalizada", (await rooms.GetAsync(created.Room.RoomId, game.Alice)).Value!.Status);
        Assert.True((await rooms.LeaveAsync(created.Room.RoomId, game.Alice)).IsSuccess);
    }
    [PostgresFact] public async Task TwoRoomsCannotStartConcurrentActiveMatchesForSameHuman()
    {
        await using var db = await fixture.CreateDatabaseAsync(); var game = new LiveMatch(db); var rooms = new InMemoryRoomService(game.NewService(), db.Source);
        var first = rooms.Create(game.Alice, "public").Value!.Room.RoomId; var second = rooms.Create(game.Alice, "public").Value!.Room.RoomId;
        rooms.Join(first, game.Bob, null); rooms.Join(second, "auth0|charlie", null);
        await rooms.SetReadyAsync(first, game.Bob, true); await rooms.SetReadyAsync(second, "auth0|charlie", true);
        var results = await Task.WhenAll(rooms.SetReadyAsync(first, game.Alice, true), rooms.SetReadyAsync(second, game.Alice, true));
        Assert.Single(results, result => result.IsSuccess); Assert.Single(results, result => result.ErrorCode == "player_already_in_match");
        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT count(*) FROM matches")); Assert.Equal(2L, await db.ScalarAsync<long>("SELECT count(*) FROM active_match_players"));
    }
    [PostgresFact] public async Task RepeatedConcurrentReadyRequestsCreateOneRoomMatchAndReturnItsId()
    {
        await using var db=await fixture.CreateDatabaseAsync();var game=new LiveMatch(db);var rooms=new InMemoryRoomService(game.NewService(),db.Source);
        var created=rooms.Create(game.Alice,"public").Value!;Assert.True(rooms.Join(created.Room.RoomId,game.Bob,null).IsSuccess);
        Assert.True((await rooms.SetReadyAsync(created.Room.RoomId,game.Bob,true)).IsSuccess);
        var results=await Task.WhenAll(rooms.SetReadyAsync(created.Room.RoomId,game.Alice,true),rooms.SetReadyAsync(created.Room.RoomId,game.Alice,true));
        Assert.All(results,result=>Assert.True(result.IsSuccess));
        Assert.Single(results.Select(result=>result.Value!.MatchId).Distinct());Assert.NotNull(results[0].Value!.MatchId);
        Assert.Equal(1L,await db.ScalarAsync<long>("SELECT count(*) FROM matches"));Assert.Equal(2L,await db.ScalarAsync<long>("SELECT count(*) FROM active_match_players"));
    }
    [PostgresFact] public async Task QuickMatchPublishesOnlyAfterDurableCreation()
    {
        await using var db = await fixture.CreateDatabaseAsync(); var game = new LiveMatch(db); var service = game.NewService();
        var queue = new MatchmakingService(game.Hub, new ConfigurationBuilder().Build(), service);
        int verified = 0;
        game.Hub.OnSend = async message =>
        {
            if (message.Event != "match.found") return;
            var match = await db.Store.GetAsync(message.Message.GetProperty("matchId").GetGuid());
            if (match is not null && match.Participants.Count == 2) Interlocked.Increment(ref verified);
        };
        await queue.JoinAsync(game.Alice, "a", Guid.NewGuid()); await queue.JoinAsync(game.Bob, "b", Guid.NewGuid());
        Assert.Equal(2, verified); Assert.Equal(1L, await db.ScalarAsync<long>("SELECT count(*) FROM matches"));
    }
    [PostgresFact] public async Task RoomAndQuickQueueDoNotConfirmCreationWhenDatabaseRejectsEvent()
    {
        await using var db = await fixture.CreateDatabaseAsync(); var game = new LiveMatch(db); var service = game.NewService();
        await FailEventsAsync(db);
        var rooms = new InMemoryRoomService(service, db.Source); var room = rooms.Create(game.Alice, "public").Value!.Room.RoomId;
        rooms.Join(room, game.Bob, null); await rooms.SetReadyAsync(room, game.Alice, true);
        var result = await rooms.SetReadyAsync(room, game.Bob, true);
        Assert.Equal("database_unavailable", result.ErrorCode); Assert.Null((await rooms.GetAsync(room, game.Alice)).Value!.MatchId);
        var queue = new MatchmakingService(game.Hub, new ConfigurationBuilder().Build(), service);
        await queue.JoinAsync(game.Alice, "a", Guid.NewGuid());
        Assert.Equal("database_unavailable", await ErrorAsync(() => queue.JoinAsync(game.Bob, "b", Guid.NewGuid())));
        Assert.DoesNotContain(game.Hub.Messages, message => message.Event == "match.found");
        Assert.Equal(0L, await db.ScalarAsync<long>("SELECT count(*) FROM matches"));
        await db.ExecuteAsync("DROP TRIGGER injected_event_failure ON match_events");
        await queue.JoinAsync(game.Bob, "b", Guid.NewGuid());
        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT count(*) FROM matches"));
    }
    [PostgresFact] public async Task AllRoundMessagesAreSentAfterCommitAndOutsideRowLock()
    {
        await using var db = await fixture.CreateDatabaseAsync(); var game = new LiveMatch(db); var service = await game.CreateAsync(); int verified = 0;
        game.Hub.OnSend = async message =>
        {
            await using var command = db.Source.CreateCommand("SELECT sequence_number FROM matches WHERE id=$1 FOR UPDATE NOWAIT");
            command.Parameters.AddWithValue(game.Initial.MatchId);
            long sequence = (long)(await command.ExecuteScalarAsync())!;
            if ((message.Event == "round.resolved" && sequence == 1) || message.Event == "round.plan.accepted") Interlocked.Increment(ref verified);
        };
        await service.SubmitAsync(game.Alice, "a", game.Command()); await service.SubmitAsync(game.Bob, "b", game.Command());
        Assert.Equal(4, verified);
    }
    [PostgresFact] public async Task TransportFailureAfterCommitDoesNotUndoRoundAndRetryRecoversResult()
    {
        await using var db = await fixture.CreateDatabaseAsync(); var game = new LiveMatch(db); var service = await game.CreateAsync();
        var a = game.Command(); await service.SubmitAsync(game.Alice, "a", a);
        game.Hub.OnSend = _ => throw new IOException("simulated transport interruption");
        await service.SubmitAsync(game.Bob, "b", game.Command());
        Assert.Equal(1, (await db.Store.GetAsync(game.Initial.MatchId))!.State.SequenceNumber);
        game.Hub.OnSend = null; game.Hub.Messages.Clear(); await game.NewService().SubmitAsync(game.Alice, "again", a);
        Assert.Equal("round.resolved", game.Hub.Messages.Last().Event);
    }

    private static Task<int> FailEventsAsync(TestDatabase db) => db.ExecuteAsync("""
        CREATE FUNCTION injected_failure() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN RAISE EXCEPTION 'injected transaction failure' USING ERRCODE='23514'; END; $$;
        CREATE TRIGGER injected_event_failure BEFORE INSERT ON match_events FOR EACH ROW EXECUTE FUNCTION injected_failure();
        """);
    private static async Task<long[]> SequencesAsync(TestDatabase db)
    {
        await using var command = db.Source.CreateCommand("SELECT sequence_number FROM match_events ORDER BY sequence_number");
        await using var reader = await command.ExecuteReaderAsync(); List<long> result = [];
        while (await reader.ReadAsync()) result.Add(reader.GetInt64(0)); return result.ToArray();
    }
    private sealed class ReplayRolls(IEnumerable<int> values) : ICriticalHitSource
    {
        private readonly Queue<int> rolls = new(values);
        public int Remaining => rolls.Count;
        public int NextBasisPointRoll() => rolls.Dequeue();
    }
}
