using Game.Domain;
using Game.Infrastructure;
using Game.Server.Rooms;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Game.Postgres.Tests;

[Trait("Category", "PostgreSQL")]
public sealed class Phase2CRecoveryTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [PostgresFact] public async Task RoomIsRecoveredByIndependentServiceInstance()
    {
        await using var db=await fixture.CreateDatabaseAsync(); var game=new LiveMatch(db); var first=new InMemoryRoomService(game.NewService(),db.Source);
        var created=first.Create(game.Alice,"private").Value!; Assert.True(first.Join(created.Room.RoomId,game.Bob,created.InviteCode).IsSuccess);
        Assert.True((await first.SetReadyAsync(created.Room.RoomId,game.Alice,true)).IsSuccess);
        var restarted=new InMemoryRoomService(game.NewService(),db.Source); var recovered=await restarted.GetAsync(created.Room.RoomId,game.Bob);
        Assert.True(recovered.IsSuccess); Assert.Equal(2,recovered.Value!.PlayerCount); Assert.True(recovered.Value.Players.Single(p=>p.PlayerId==created.Room.Players[0].PlayerId).IsReady);
    }

    [PostgresFact] public async Task ActiveMatchIsDiscoveredAndSnapshotReportsPendingPlanAfterRestart()
    {
        await using var db=await fixture.CreateDatabaseAsync();var game=new LiveMatch(db);var service=await game.CreateAsync();
        await service.SubmitAsync(game.Alice,"old",game.Command());
        await using var pool=PostgresServices.CreateDataSource(db.ConnectionString);var store=new PostgresMatchSessionStore(pool,NullLogger<PostgresMatchSessionStore>.Instance);
        var restarted=new MatchRoundService(new RecordingGameHub(),new CountingRolls(),store);
        Assert.Equal(game.Initial.MatchId,await restarted.FindLatestMatchAsync(game.Alice));
        var snapshot=System.Text.Json.JsonSerializer.SerializeToElement(await restarted.AttachAsync(game.Initial.MatchId,game.Alice,"new"));
        Assert.True(snapshot.GetProperty("hasSubmittedPlan").GetBoolean()); Assert.Equal(0,snapshot.GetProperty("sequence").GetInt64());
        Assert.False(snapshot.GetProperty("opponent").TryGetProperty("energy",out _));
    }

    [PostgresFact] public async Task PlansSubmittedAcrossIndependentInstancesResolveOnce()
    {
        await using var db=await fixture.CreateDatabaseAsync();var game=new LiveMatch(db);var first=await game.CreateAsync();
        await first.SubmitAsync(game.Alice,"a",game.Command()); await using var pool=PostgresServices.CreateDataSource(db.ConnectionString);
        var second=new MatchRoundService(new RecordingGameHub(),new CountingRolls(),new PostgresMatchSessionStore(pool,NullLogger<PostgresMatchSessionStore>.Instance));
        await second.SubmitAsync(game.Bob,"b",game.Command()); Assert.Equal(1,(await db.Store.GetAsync(game.Initial.MatchId))!.State.SequenceNumber);
        Assert.Equal(1L,await db.ScalarAsync<long>("SELECT count(*) FROM match_events WHERE event_type='round.resolved'"));
    }

    [PostgresFact] public async Task UnauthorizedReconnectIsRejectedWithoutChangingState()
    {
        await using var db=await fixture.CreateDatabaseAsync();var game=new LiveMatch(db);var service=await game.CreateAsync();
        await Assert.ThrowsAsync<Microsoft.AspNetCore.SignalR.HubException>(()=>service.AttachAsync(game.Initial.MatchId,"auth0|intruder","x"));
        Assert.Equal(0,(await db.Store.GetAsync(game.Initial.MatchId))!.State.SequenceNumber);
    }

    [PostgresFact] public async Task FinishedResultAndReasonAreRecoveredUnchanged()
    {
        await using var db=await fixture.CreateDatabaseAsync();var game=new LiveMatch(db);var service=await game.CreateAsync();await service.AbandonAsync(game.Initial.MatchId,game.Bob);
        var restarted=game.NewService();var snapshot=System.Text.Json.JsonSerializer.SerializeToElement(await restarted.AttachAsync(game.Initial.MatchId,game.Alice,"new"));
        Assert.Equal("completed",snapshot.GetProperty("status").GetString());Assert.Equal("abandonment",snapshot.GetProperty("terminalReason").GetString());
        Assert.Equal(game.Initial.FirstPlayer.PlayerId,snapshot.GetProperty("winnerId").GetGuid());
    }

    [PostgresFact] public async Task FailedTransportLeavesOutboxAndRestartDeliversItOnlyOnce()
    {
        await using var db=await fixture.CreateDatabaseAsync();var game=new LiveMatch(db);var service=await game.CreateAsync();await service.SubmitAsync(game.Alice,"a",game.Command());
        game.Hub.OnSend=_=>throw new IOException("transport");await service.SubmitAsync(game.Bob,"b",game.Command());
        Assert.Equal(2L,await db.ScalarAsync<long>("SELECT count(*) FROM match_outbox WHERE delivered_at IS NULL"));
        var hub=new RecordingGameHub();var restarted=new MatchRoundService(hub,new CountingRolls(),game.Store);await restarted.AttachAsync(game.Initial.MatchId,game.Alice,"alice-new");await restarted.AttachAsync(game.Initial.MatchId,game.Bob,"bob-new");
        Assert.Equal(2,await restarted.DeliverPendingAsync(game.Initial.MatchId));Assert.Equal(0,await restarted.DeliverPendingAsync(game.Initial.MatchId));
        Assert.Equal(2,hub.Messages.Count(m=>m.Event=="round.resolved"));
    }

    [PostgresFact] public async Task PersistedDeadlineIsProcessedIdempotentlyAfterRestart()
    {
        await using var db=await fixture.CreateDatabaseAsync();var game=new LiveMatch(db);await game.CreateAsync();
        DateTimeOffset deadline=new(await db.ScalarAsync<DateTime>("SELECT round_deadline_at FROM matches WHERE id=$1",game.Initial.MatchId));
        var restarted=game.NewService();await Task.WhenAll(restarted.ExpireInactiveMatchesAsync(deadline.AddSeconds(1),TimeSpan.FromMinutes(5),default),game.NewService().ExpireInactiveMatchesAsync(deadline.AddSeconds(1),TimeSpan.FromMinutes(5),default));
        Assert.Equal(MatchStatus.Finished,(await db.Store.GetAsync(game.Initial.MatchId))!.State.Status);Assert.Equal(1L,await db.ScalarAsync<long>("SELECT count(*) FROM match_events WHERE event_type='match.ended'"));
    }

    [PostgresFact] public async Task DeadlineAndSecondCommandCannotResolveTwice()
    {
        await using var db=await fixture.CreateDatabaseAsync();var game=new LiveMatch(db);var first=await game.CreateAsync();await first.SubmitAsync(game.Alice,"a",game.Command());
        DateTimeOffset deadline=new(await db.ScalarAsync<DateTime>("SELECT round_deadline_at FROM matches WHERE id=$1",game.Initial.MatchId));
        Task timer=game.NewService().ExpireInactiveMatchesAsync(deadline.AddSeconds(1),TimeSpan.FromMinutes(5),default);
        Task command=game.NewService().SubmitAsync(game.Bob,"b",game.Command()).ContinueWith(_=>{},TaskScheduler.Default);await Task.WhenAll(timer,command);
        Assert.Equal(1,(await db.Store.GetAsync(game.Initial.MatchId))!.State.SequenceNumber);Assert.Equal(2L,await db.ScalarAsync<long>("SELECT count(*) FROM match_events"));
    }

    [PostgresFact] public async Task QueueIsExplicitlyEmptyInNewServiceInstance()
    {
        await using var db=await fixture.CreateDatabaseAsync();var game=new LiveMatch(db);var firstHub=new RecordingGameHub();var first=new MatchmakingService(firstHub,new ConfigurationBuilder().Build(),game.NewService());
        await first.JoinAsync(game.Alice,"a",Guid.NewGuid());var secondHub=new RecordingGameHub();var restarted=new MatchmakingService(secondHub,new ConfigurationBuilder().Build(),game.NewService());await restarted.JoinAsync(game.Bob,"b",Guid.NewGuid());
        Assert.Equal(0L,await db.ScalarAsync<long>("SELECT count(*) FROM matches"));Assert.Contains(firstHub.Messages,m=>m.Event=="matchmaking.status");Assert.Contains(secondHub.Messages,m=>m.Event=="matchmaking.status");
    }

    [PostgresFact] public async Task ExpiredWaitingRoomIsClosedAndHiddenAfterRestart()
    {
        await using var db=await fixture.CreateDatabaseAsync();var game=new LiveMatch(db);var rooms=new InMemoryRoomService(game.NewService(),db.Source);var id=rooms.Create(game.Alice,"public").Value!.Room.RoomId;
        await db.ExecuteAsync("UPDATE rooms SET last_activity_at=CURRENT_TIMESTAMP-interval '10 minutes' WHERE id=$1",id);Assert.Equal(1,rooms.RemoveInactiveRooms(DateTimeOffset.UtcNow,TimeSpan.FromMinutes(5)));
        var restarted=new InMemoryRoomService(game.NewService(),db.Source);Assert.False((await restarted.GetAsync(id,game.Alice)).IsSuccess);Assert.DoesNotContain(restarted.ListPublicRooms(game.Bob),r=>r.RoomId==id);
    }
}
