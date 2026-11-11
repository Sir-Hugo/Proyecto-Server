using System.Text.Json;
using Game.Domain;
using Game.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Game.Postgres.Tests;

[Trait("Category", "PostgreSQL")]
public sealed class PostgresPersistenceTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static StoredMatch Match()
    {
        // Fixed microsecond precision matches PostgreSQL timestamptz storage exactly.
        var now = DateTimeOffset.Parse("2026-11-10T12:00:00Z");
        var state = MatchState.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), now);
        return new(state, [new(state.FirstPlayer.PlayerId, "auth0|alice-external", null, 1),
            new(state.SecondPlayer.PlayerId, "google-oauth2|bob-external", "Bob", 2)], now, null);
    }
    private static StoredEvent Event(StoredMatch match) => new(match.State.MatchId, 0, "match.started", 1,
        match.CreatedAt, JsonSerializer.SerializeToElement(new { state = match.State, reason = "created" }));
    private static StoredPendingPlan Plan(StoredMatch match) => new(match.State.MatchId, match.State.FirstPlayer.PlayerId,
        Guid.NewGuid(), 1, 0, [GameAction.Attack], JsonSerializer.SerializeToElement(new { type = "round.plan.accepted", sequence = 0 }));

    [PostgresFact] public async Task CreatesAllFiveTablesFromEmptyDatabase()
    {
        await using var db = await fixture.CreateDatabaseAsync(false);
        Assert.Empty(await db.Migrator.GetVersionsAsync());
        await db.Migrator.ApplyAsync();
        Assert.Equal(3, (await db.Migrator.GetVersionsAsync()).Max(version => version.Version));
        Assert.Equal(5L, await db.ScalarAsync<long>("SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public' AND table_name IN ('matches','match_participants','match_events','pending_round_plans','match_commands')"));
    }
    [PostgresFact] public async Task RepeatedAndConcurrentMigrationsPreserveData()
    {
        await using var db = await fixture.CreateDatabaseAsync(); var match = Match();
        await db.Store.CreateAsync(match); await db.Store.AppendEventAsync(Event(match));
        await Task.WhenAll(db.Migrator.ApplyAsync(), db.Migrator.ApplyAsync());
        Assert.Equal(3, (await db.Migrator.GetVersionsAsync()).Count);
        Assert.Equal(match.State, (await db.Store.GetAsync(match.State.MatchId))!.State);
        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT count(*) FROM match_events"));
    }
    [PostgresFact] public async Task RoundTripsStateAndTextAuthSubjects()
    {
        await using var db = await fixture.CreateDatabaseAsync(); var match = Match();
        await db.Store.CreateAsync(match); var loaded = Assert.IsType<StoredMatch>(await db.Store.GetAsync(match.State.MatchId));
        Assert.Equal(match.State, loaded.State); Assert.Equal(match.CreatedAt, loaded.CreatedAt);
        Assert.Equal(match.Participants, loaded.Participants); Assert.Null(loaded.CompletedAt);
        Assert.Null(await db.Store.GetAsync(Guid.NewGuid()));
    }
    [PostgresFact] public async Task EventsHaveVersionSequenceAndReplayState()
    {
        await using var db = await fixture.CreateDatabaseAsync(); var match = Match();
        await db.Store.CreateAsync(match); await db.Store.AppendEventAsync(Event(match));
        Assert.Equal(0L, await db.ScalarAsync<long>("SELECT sequence_number FROM match_events WHERE match_id=$1", match.State.MatchId));
        Assert.Equal((short)1, await db.ScalarAsync<short>("SELECT event_version FROM match_events WHERE match_id=$1", match.State.MatchId));
        string payload = await db.ScalarAsync<string>("SELECT payload::text FROM match_events WHERE match_id=$1", match.State.MatchId);
        Assert.Equal(match.State, JsonSerializer.Deserialize<JsonElement>(payload).GetProperty("state").Deserialize<MatchState>());
    }
    [PostgresFact] public async Task DuplicateEventSequenceRejected()
    {
        await using var db = await fixture.CreateDatabaseAsync(); var match = Match();
        await db.Store.CreateAsync(match); await db.Store.AppendEventAsync(Event(match));
        Assert.Equal("23505", (await Assert.ThrowsAsync<PersistenceException>(() => db.Store.AppendEventAsync(Event(match)))).SqlState);
    }
    [PostgresTheory] [InlineData("UPDATE match_events SET event_version=2")] [InlineData("DELETE FROM match_events")] [InlineData("TRUNCATE match_events")]
    public async Task EventsAreAppendOnly(string sql)
    {
        await using var db = await fixture.CreateDatabaseAsync(); var match = Match();
        await db.Store.CreateAsync(match); await db.Store.AppendEventAsync(Event(match));
        Assert.Equal("23514", (await Assert.ThrowsAsync<PostgresException>(() => db.ExecuteAsync(sql))).SqlState);
        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT count(*) FROM match_events"));
    }
    [PostgresTheory] [InlineData("player")] [InlineData("subject")] [InlineData("slot")]
    public async Task DuplicateParticipantKeysRejected(string duplicate)
    {
        await using var db = await fixture.CreateDatabaseAsync(); var match = Match(); await db.Store.CreateAsync(match);
        var first = match.Participants[0];
        string column = duplicate switch { "player" => "player_id", "subject" => "auth_subject", _ => "slot" };
        object value = duplicate switch { "player" => first.PlayerId, "subject" => first.AuthSubject, _ => (short)1 };
        // Change one key only so an unrelated UNIQUE cannot accidentally make this test pass.
        var error = await Assert.ThrowsAsync<PostgresException>(() => db.ExecuteAsync(
            $"UPDATE match_participants SET {column}=$1 WHERE match_id=$2 AND slot=2", value, match.State.MatchId));
        Assert.Equal("23505", error.SqlState);
    }
    [PostgresTheory]
    [InlineData("first_health", -1)] [InlineData("second_health", 51)]
    [InlineData("first_energy", -1)] [InlineData("second_energy", -1)]
    [InlineData("first_reserved_energy", -1)] [InlineData("second_reserved_energy", -1)]
    [InlineData("first_used_blocks", -1)] [InlineData("second_used_blocks", 6)]
    [InlineData("round_number", 0)] [InlineData("round_number", 11)] [InlineData("sequence_number", -1)]
    public async Task RejectsInvalidStateRanges(string column, int value)
    {
        await using var db = await fixture.CreateDatabaseAsync(); var match = Match(); await db.Store.CreateAsync(match);
        // Column is controlled by InlineData, not external input. Values are parameters.
        Assert.Equal("23514", (await Assert.ThrowsAsync<PostgresException>(() => db.ExecuteAsync($"UPDATE matches SET {column}=$1 WHERE id=$2", value, match.State.MatchId))).SqlState);
    }
    [PostgresFact] public async Task EnergyHasNoArbitraryReserveLimit()
    {
        await using var db = await fixture.CreateDatabaseAsync(); var match = Match(); await db.Store.CreateAsync(match);
        await db.ExecuteAsync("UPDATE matches SET first_energy=10000 WHERE id=$1", match.State.MatchId);
        Assert.Equal(10000, (await db.Store.GetAsync(match.State.MatchId))!.State.FirstPlayer.Energy);
    }
    [PostgresTheory] [InlineData("missing")] [InlineData("swapped")]
    public async Task RequiresExactlyTwoDeclaredParticipantSlotsAtCommit(string scenario)
    {
        await using var db = await fixture.CreateDatabaseAsync(); var match = Match();
        var invalid = match with { Participants = scenario == "missing" ? [match.Participants[0]] :
            [match.Participants[0] with { Slot = 2 }, match.Participants[1] with { Slot = 1 }] };
        Assert.Equal("23514", (await Assert.ThrowsAsync<PersistenceException>(() => db.Store.CreateAsync(invalid))).SqlState);
        Assert.Null(await db.Store.GetAsync(match.State.MatchId));
        Assert.Equal(0L, await db.ScalarAsync<long>("SELECT count(*) FROM matches"));
    }
    [PostgresFact] public async Task WinnerAndCompletionMustMatchStatusAndParticipants()
    {
        await using var db = await fixture.CreateDatabaseAsync(); var match = Match(); await db.Store.CreateAsync(match);
        Assert.Equal("23514", (await Assert.ThrowsAsync<PostgresException>(() => db.ExecuteAsync("UPDATE matches SET winner_player_id=$1 WHERE id=$2", match.State.FirstPlayer.PlayerId, match.State.MatchId))).SqlState);
        Assert.Equal("23514", (await Assert.ThrowsAsync<PostgresException>(() => db.ExecuteAsync("UPDATE matches SET status='completed' WHERE id=$1", match.State.MatchId))).SqlState);
        Assert.Equal("23514", (await Assert.ThrowsAsync<PostgresException>(() => db.ExecuteAsync("UPDATE matches SET status='completed',completed_at=created_at,winner_player_id=$1 WHERE id=$2", Guid.NewGuid(), match.State.MatchId))).SqlState);
        await db.ExecuteAsync("UPDATE matches SET status='completed',completed_at=created_at,winner_player_id=first_player_id WHERE id=$1", match.State.MatchId);
        Assert.Equal(match.State.FirstPlayer.PlayerId, (await db.Store.GetAsync(match.State.MatchId))!.State.WinnerId);
    }
    [PostgresFact] public async Task StoresAndReadsOnlyRequestedPlayersPrivatePlan()
    {
        await using var db = await fixture.CreateDatabaseAsync(); var match = Match(); await db.Store.CreateAsync(match);
        var plan = Plan(match); await db.Store.SavePendingPlanAsync(plan);
        var loaded = Assert.IsType<StoredPendingPlan>(await db.Store.GetPendingPlanAsync(plan.MatchId, plan.PlayerId));
        Assert.Equal(plan.RequestId, loaded.RequestId); Assert.Equal(plan.Actions, loaded.Actions);
        Assert.True(JsonElement.DeepEquals(plan.Acknowledgement, loaded.Acknowledgement));
        Assert.Null(await db.Store.GetPendingPlanAsync(plan.MatchId, match.State.SecondPlayer.PlayerId));
    }
    [PostgresFact] public async Task RequestIdUniquePerMatchAndPlayer()
    {
        await using var db = await fixture.CreateDatabaseAsync(); var match = Match(); await db.Store.CreateAsync(match);
        var plan = Plan(match); await db.Store.SavePendingPlanAsync(plan);
        Assert.Equal("23505", (await Assert.ThrowsAsync<PersistenceException>(() => db.Store.SavePendingPlanAsync(plan))).SqlState);
        await db.Store.SavePendingPlanAsync(plan with { PlayerId = match.State.SecondPlayer.PlayerId });
        await db.ExecuteAsync("UPDATE matches SET status='completed',completed_at=created_at WHERE id=$1", match.State.MatchId);
        var other = Match(); await db.Store.CreateAsync(other);
        await db.Store.SavePendingPlanAsync(Plan(other) with { RequestId = plan.RequestId });
        Assert.Equal(3L, await db.ScalarAsync<long>("SELECT count(*) FROM match_commands"));
    }
    [PostgresFact] public async Task FailedPlanRollsBackItsCommand()
    {
        await using var db = await fixture.CreateDatabaseAsync(); var match = Match(); await db.Store.CreateAsync(match);
        var plan = Plan(match) with { Round = 2 };
        Assert.Equal("23514", (await Assert.ThrowsAsync<PersistenceException>(() => db.Store.SavePendingPlanAsync(plan))).SqlState);
        Assert.Equal(0L, await db.ScalarAsync<long>("SELECT count(*) FROM match_commands"));
        Assert.Equal(0L, await db.ScalarAsync<long>("SELECT count(*) FROM pending_round_plans"));
    }
    [PostgresFact] public async Task WrongSequenceAndSecondPendingPlanAreRejectedAtomically()
    {
        await using var db = await fixture.CreateDatabaseAsync(); var match = Match(); await db.Store.CreateAsync(match);
        var plan = Plan(match);
        Assert.Equal("23514", (await Assert.ThrowsAsync<PersistenceException>(() => db.Store.SavePendingPlanAsync(plan with { ExpectedSequence = 1 }))).SqlState);
        await db.Store.SavePendingPlanAsync(plan);
        Assert.Equal("23505", (await Assert.ThrowsAsync<PersistenceException>(() => db.Store.SavePendingPlanAsync(plan with { RequestId = Guid.NewGuid() }))).SqlState);
        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT count(*) FROM match_commands"));
        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT count(*) FROM pending_round_plans"));
    }
    [PostgresFact] public async Task ForeignPlayerCannotOwnCommandOrPendingPlan()
    {
        await using var db = await fixture.CreateDatabaseAsync(); var match = Match(); await db.Store.CreateAsync(match);
        Assert.Equal("23503", (await Assert.ThrowsAsync<PersistenceException>(() => db.Store.SavePendingPlanAsync(Plan(match) with { PlayerId = Guid.NewGuid() }))).SqlState);
        Assert.Equal(0L, await db.ScalarAsync<long>("SELECT count(*) FROM match_commands"));
    }
    [PostgresFact] public async Task FailedParticipantInsertRollsBackEntireMatch()
    {
        await using var db = await fixture.CreateDatabaseAsync(); var match = Match();
        var invalid = match with { Participants = [match.Participants[0], match.Participants[1] with { AuthSubject = match.Participants[0].AuthSubject }] };
        Assert.Equal("23505", (await Assert.ThrowsAsync<PersistenceException>(() => db.Store.CreateAsync(invalid))).SqlState);
        Assert.Equal(0L, await db.ScalarAsync<long>("SELECT count(*) FROM matches"));
        Assert.Equal(0L, await db.ScalarAsync<long>("SELECT count(*) FROM match_participants"));
    }
    [PostgresFact] public async Task DataSurvivesNewConnectionAndNewPool()
    {
        await using var db = await fixture.CreateDatabaseAsync(); var match = Match(); await db.Store.CreateAsync(match);
        await db.Store.SavePendingPlanAsync(Plan(match));
        await using var freshSource = PostgresServices.CreateDataSource(db.ConnectionString);
        var freshStore = new PostgresMatchStore(freshSource, NullLogger<PostgresMatchStore>.Instance);
        Assert.Equal(match.State, (await freshStore.GetAsync(match.State.MatchId))!.State);
        Assert.NotNull(await freshStore.GetPendingPlanAsync(match.State.MatchId, match.State.FirstPlayer.PlayerId));
    }
    [PostgresFact] public async Task MigrationHistoryTamperingIsDetected()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await db.ExecuteAsync("UPDATE schema_migrations SET checksum=repeat('0',64)");
        Assert.Contains("migration history mismatch", (await Assert.ThrowsAsync<PersistenceException>(() => db.Migrator.ApplyAsync())).Message);
    }
    [PostgresFact] public async Task MigrationFailureRollsBackHistoryAndPartialSchema()
    {
        await using var db = await fixture.CreateDatabaseAsync(false);
        await db.ExecuteAsync("CREATE TABLE match_events (sentinel integer)");
        Assert.Equal("42P07", (await Assert.ThrowsAsync<PersistenceException>(() => db.Migrator.ApplyAsync())).SqlState);
        Assert.True(await db.ScalarAsync<bool>("SELECT to_regclass('matches') IS NULL AND to_regclass('schema_migrations') IS NULL"));
        await db.ExecuteAsync("INSERT INTO match_events VALUES (7)");
        Assert.Equal(7, await db.ScalarAsync<int>("SELECT sentinel FROM match_events"));
    }
}
