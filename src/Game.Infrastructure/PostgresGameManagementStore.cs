using System.Text.Json;
using Game.Domain;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace Game.Infrastructure;

public sealed record PageResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, long Total);
public sealed record MatchHistoryItem(Guid MatchId, string Status, int RoundNumber, long Sequence,
    Guid YourPlayerId, string? YourNickname, Guid OpponentPlayerId, string? OpponentNickname,
    string Result, DateTimeOffset CreatedAt, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt, string? TerminalReason);
public sealed record MatchParticipantView(Guid PlayerId, string? Nickname, short Slot);
public sealed record MatchDetailView(Guid MatchId, string Status, int RoundNumber, long Sequence,
    Guid? WinnerPlayerId, string? TerminalReason, DateTimeOffset CreatedAt, DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt, IReadOnlyList<MatchParticipantView> Participants, Guid RequestingPlayerId);
public sealed record ReplayEvent(long Sequence, string Type, short Version, DateTimeOffset OccurredAt, JsonElement Payload);
public sealed record ActiveMatchView(Guid MatchId, int RoundNumber, long Sequence, DateTimeOffset LastActivityAt,
    DateTimeOffset? RoundDeadlineAt, IReadOnlyList<string> ParticipantSubjects);
public sealed record AdminAuditView(Guid Id, string ActorSubject, string Action, string? TargetSubject,
    Guid? MatchId, string Reason, DateTimeOffset OccurredAt);

public sealed class PostgresGameManagementStore(NpgsqlDataSource source, ILogger<PostgresGameManagementStore> logger)
{
    public Task<PageResult<MatchHistoryItem>> GetHistoryAsync(string subject, int page, int pageSize,
        CancellationToken cancellationToken = default) => GuardAsync<PageResult<MatchHistoryItem>>("read match history", async () =>
    {
        page = Math.Max(1, page); pageSize = Math.Clamp(pageSize, 1, 100);
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var count = new NpgsqlCommand("SELECT count(*) FROM match_participants WHERE auth_subject=$1", connection);
        count.Parameters.AddWithValue(subject);
        long total = (long)(await count.ExecuteScalarAsync(cancellationToken))!;
        await using var command = new NpgsqlCommand("""
            SELECT m.id,m.status,m.round_number,m.sequence_number,own.player_id,own.nickname,
                   opponent.player_id,opponent.nickname,m.winner_player_id,m.created_at,m.started_at,m.completed_at,m.terminal_reason
            FROM match_participants own
            JOIN matches m ON m.id=own.match_id
            JOIN match_participants opponent ON opponent.match_id=m.id AND opponent.player_id<>own.player_id
            WHERE own.auth_subject=$1
            ORDER BY COALESCE(m.completed_at,m.started_at,m.created_at) DESC,m.id
            LIMIT $2 OFFSET $3
            """, connection);
        command.Parameters.AddWithValue(subject); command.Parameters.AddWithValue(pageSize); command.Parameters.AddWithValue((page - 1) * pageSize);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        List<MatchHistoryItem> items=[];
        while(await reader.ReadAsync(cancellationToken))
        {
            Guid own=reader.GetGuid(4); Guid? winner=reader.IsDBNull(8)?null:reader.GetGuid(8);
            string result=reader.GetString(1)=="active"?"active":winner is null?"draw":winner==own?"won":"lost";
            items.Add(new(reader.GetGuid(0),reader.GetString(1),reader.GetInt16(2),reader.GetInt64(3),own,
                reader.IsDBNull(5)?null:reader.GetString(5),reader.GetGuid(6),reader.IsDBNull(7)?null:reader.GetString(7),result,
                reader.GetFieldValue<DateTimeOffset>(9),reader.IsDBNull(10)?null:reader.GetFieldValue<DateTimeOffset>(10),
                reader.IsDBNull(11)?null:reader.GetFieldValue<DateTimeOffset>(11),reader.IsDBNull(12)?null:reader.GetString(12)));
        }
        return new(items,page,pageSize,total);
    });

    public Task<MatchDetailView?> GetDetailAsync(string subject, Guid matchId, CancellationToken cancellationToken = default) =>
        GuardAsync<MatchDetailView?>("read private match detail", async () =>
        {
            await using var connection=await source.OpenConnectionAsync(cancellationToken);
            await using var command=new NpgsqlCommand("""
                SELECT m.id,m.status,m.round_number,m.sequence_number,m.winner_player_id,m.terminal_reason,
                       m.created_at,m.started_at,m.completed_at,p.player_id,p.nickname,p.slot,own.player_id
                FROM matches m JOIN match_participants own ON own.match_id=m.id AND own.auth_subject=$1
                JOIN match_participants p ON p.match_id=m.id WHERE m.id=$2 ORDER BY p.slot
                """,connection);
            command.Parameters.AddWithValue(subject);command.Parameters.AddWithValue(matchId);
            await using var reader=await command.ExecuteReaderAsync(cancellationToken);
            MatchDetailView? result=null;List<MatchParticipantView> participants=[];
            while(await reader.ReadAsync(cancellationToken))
            {
                participants.Add(new(reader.GetGuid(9),reader.IsDBNull(10)?null:reader.GetString(10),reader.GetInt16(11)));
                result ??= new(reader.GetGuid(0),reader.GetString(1),reader.GetInt16(2),reader.GetInt64(3),
                    reader.IsDBNull(4)?null:reader.GetGuid(4),reader.IsDBNull(5)?null:reader.GetString(5),reader.GetFieldValue<DateTimeOffset>(6),
                    reader.IsDBNull(7)?null:reader.GetFieldValue<DateTimeOffset>(7),reader.IsDBNull(8)?null:reader.GetFieldValue<DateTimeOffset>(8),participants,reader.GetGuid(12));
            }
            return result;
        });

    public Task<IReadOnlyList<ReplayEvent>?> GetReplayAsync(string subject, Guid matchId, CancellationToken cancellationToken = default) =>
        GuardAsync<IReadOnlyList<ReplayEvent>?>("read private match replay", async () =>
        {
            await using var connection=await source.OpenConnectionAsync(cancellationToken);
            await using var authorized=new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM match_participants WHERE match_id=$1 AND auth_subject=$2)",connection);
            authorized.Parameters.AddWithValue(matchId);authorized.Parameters.AddWithValue(subject);
            if(!(bool)(await authorized.ExecuteScalarAsync(cancellationToken))!) return null;
            await using var command=new NpgsqlCommand("SELECT sequence_number,event_type,event_version,occurred_at,payload::text FROM match_events WHERE match_id=$1 ORDER BY sequence_number",connection);
            command.Parameters.AddWithValue(matchId);await using var reader=await command.ExecuteReaderAsync(cancellationToken);List<ReplayEvent> events=[];
            while(await reader.ReadAsync(cancellationToken)) events.Add(new(reader.GetInt64(0),reader.GetString(1),reader.GetInt16(2),reader.GetFieldValue<DateTimeOffset>(3),JsonSerializer.Deserialize<JsonElement>(reader.GetString(4))));
            return events;
        });

    public Task<bool> IsSuspendedAsync(string subject, CancellationToken cancellationToken = default) => GuardAsync("check suspension",async()=>
    {
        await using var command=source.CreateCommand("SELECT EXISTS(SELECT 1 FROM user_suspensions WHERE auth_subject=$1 AND (expires_at IS NULL OR expires_at>CURRENT_TIMESTAMP))");
        command.Parameters.AddWithValue(subject);return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    });

    public Task SuspendAsync(string actor,string subject,string reason,DateTimeOffset? expiresAt,CancellationToken cancellationToken=default)=>GuardAsync("suspend user",async()=>
    {
        await using var connection=await source.OpenConnectionAsync(cancellationToken);await using var tx=await connection.BeginTransactionAsync(cancellationToken);
        await using(var command=new NpgsqlCommand("""
            INSERT INTO user_suspensions(auth_subject,reason,suspended_by,expires_at) VALUES($1,$2,$3,$4)
            ON CONFLICT(auth_subject) DO UPDATE SET reason=EXCLUDED.reason,suspended_by=EXCLUDED.suspended_by,suspended_at=CURRENT_TIMESTAMP,expires_at=EXCLUDED.expires_at
            """,connection,tx))
        {command.Parameters.AddWithValue(subject);command.Parameters.AddWithValue(reason);command.Parameters.AddWithValue(actor);command.Parameters.Add(new NpgsqlParameter{NpgsqlDbType=NpgsqlDbType.TimestampTz,Value=(object?)expiresAt?.ToUniversalTime()??DBNull.Value});await command.ExecuteNonQueryAsync(cancellationToken);}
        await InsertAuditAsync(connection,tx,actor,"user.suspended",subject,null,reason,cancellationToken);await tx.CommitAsync(cancellationToken);return true;
    });

    public Task<bool> UnsuspendAsync(string actor,string subject,string reason,CancellationToken cancellationToken=default)=>GuardAsync("unsuspend user",async()=>
    {
        await using var connection=await source.OpenConnectionAsync(cancellationToken);await using var tx=await connection.BeginTransactionAsync(cancellationToken);
        await using var delete=new NpgsqlCommand("DELETE FROM user_suspensions WHERE auth_subject=$1",connection,tx);delete.Parameters.AddWithValue(subject);
        bool removed=await delete.ExecuteNonQueryAsync(cancellationToken)>0;if(removed)await InsertAuditAsync(connection,tx,actor,"user.unsuspended",subject,null,reason,cancellationToken);
        await tx.CommitAsync(cancellationToken);return removed;
    });

    public Task<IReadOnlyList<ActiveMatchView>> GetActiveMatchesAsync(int limit,CancellationToken cancellationToken=default)=>GuardAsync<IReadOnlyList<ActiveMatchView>>("read active matches",async()=>
    {
        await using var command=source.CreateCommand("""
            SELECT m.id,m.round_number,m.sequence_number,m.last_activity_at,m.round_deadline_at,array_agg(p.auth_subject ORDER BY p.slot)
            FROM matches m JOIN match_participants p ON p.match_id=m.id WHERE m.status='active'
            GROUP BY m.id ORDER BY m.last_activity_at DESC LIMIT $1
            """);command.Parameters.AddWithValue(Math.Clamp(limit,1,200));await using var reader=await command.ExecuteReaderAsync(cancellationToken);List<ActiveMatchView> result=[];
        while(await reader.ReadAsync(cancellationToken))result.Add(new(reader.GetGuid(0),reader.GetInt16(1),reader.GetInt64(2),reader.GetFieldValue<DateTimeOffset>(3),reader.IsDBNull(4)?null:reader.GetFieldValue<DateTimeOffset>(4),reader.GetFieldValue<string[]>(5)));
        return result;
    });

    public Task<bool> TerminateMatchAsync(string actor,Guid matchId,string reason,CancellationToken cancellationToken=default)=>GuardAsync("administratively terminate match",async()=>
    {
        await using var connection=await source.OpenConnectionAsync(cancellationToken);await using var tx=await connection.BeginTransactionAsync(cancellationToken);
        await using(var gate=new NpgsqlCommand("SELECT id FROM matches WHERE id=$1 FOR UPDATE",connection,tx)){gate.Parameters.AddWithValue(matchId);if(await gate.ExecuteScalarAsync(cancellationToken)is null)return false;}
        var stored=await PostgresMatchStore.ReadAsync(connection,tx,matchId,cancellationToken);if(stored is null||stored.State.Status!=MatchStatus.Active)return false;
        var utcNow=DateTime.UtcNow;var now=new DateTimeOffset(utcNow.Ticks-utcNow.Ticks%10,TimeSpan.Zero);var before=stored.State;
        var after=before with{Status=MatchStatus.Finished,WinnerId=null,SequenceNumber=before.SequenceNumber+1,LastActivityAt=now};
        await using(var update=new NpgsqlCommand("UPDATE matches SET status='completed',sequence_number=$1,winner_player_id=NULL,last_activity_at=$2,completed_at=$2,round_deadline_at=NULL,terminal_reason='administrative' WHERE id=$3",connection,tx))
        {update.Parameters.AddWithValue(after.SequenceNumber);update.Parameters.AddWithValue(now);update.Parameters.AddWithValue(matchId);await update.ExecuteNonQueryAsync(cancellationToken);}
        await using(var plans=new NpgsqlCommand("DELETE FROM pending_round_plans WHERE match_id=$1",connection,tx)){plans.Parameters.AddWithValue(matchId);await plans.ExecuteNonQueryAsync(cancellationToken);}
        await using(var evt=new NpgsqlCommand("INSERT INTO match_events(match_id,sequence_number,event_type,event_version,occurred_at,payload) VALUES($1,$2,'match.ended',1,$3,$4)",connection,tx))
        {evt.Parameters.AddWithValue(matchId);evt.Parameters.AddWithValue(after.SequenceNumber);evt.Parameters.AddWithValue(now);evt.Parameters.AddWithValue(NpgsqlDbType.Jsonb,JsonSerializer.Serialize(new{before,after,reason="administrative"}));await evt.ExecuteNonQueryAsync(cancellationToken);}
        foreach(var participant in stored.Participants)
        {
            var opponent=stored.Participants.Single(p=>p.PlayerId!=participant.PlayerId);var you=participant.PlayerId==after.FirstPlayer.PlayerId?after.FirstPlayer:after.SecondPlayer;var other=you.PlayerId==after.FirstPlayer.PlayerId?after.SecondPlayer:after.FirstPlayer;
            var payload=JsonSerializer.Serialize(new{type="match.ended",version=1,matchId,sequence=after.SequenceNumber,roundNumber=after.RoundNumber,reason="administrative",status="completed",winnerId=(Guid?)null,hasSubmittedPlan=false,roundDeadlineAt=(DateTimeOffset?)null,terminalReason="administrative",you=new{playerId=you.PlayerId,isHost=you.PlayerId==after.FirstPlayer.PlayerId,nickname=participant.Nickname,health=you.Health,energy=you.Energy,usedBlockades=you.UsedBlockades,hasUsedSpecialAttack=you.HasUsedSpecialAttack},opponent=new{playerId=other.PlayerId,isHost=other.PlayerId==after.FirstPlayer.PlayerId,nickname=opponent.Nickname,health=other.Health}});
            await using var outbox=new NpgsqlCommand("INSERT INTO match_outbox(id,match_id,target_subject,event_type,sequence_number,payload,created_at,next_attempt_at) VALUES($1,$2,$3,'match.ended',$4,$5,$6,$6)",connection,tx);
            outbox.Parameters.AddWithValue(Guid.NewGuid());outbox.Parameters.AddWithValue(matchId);outbox.Parameters.AddWithValue(participant.AuthSubject);outbox.Parameters.AddWithValue(after.SequenceNumber);outbox.Parameters.AddWithValue(NpgsqlDbType.Jsonb,payload);outbox.Parameters.AddWithValue(now);await outbox.ExecuteNonQueryAsync(cancellationToken);
        }
        await InsertAuditAsync(connection,tx,actor,"match.terminated",null,matchId,reason,cancellationToken);await tx.CommitAsync(cancellationToken);return true;
    });

    public Task<PageResult<AdminAuditView>> GetAuditAsync(int page,int pageSize,CancellationToken cancellationToken=default)=>GuardAsync<PageResult<AdminAuditView>>("read administrative audit",async()=>
    {
        page=Math.Max(1,page);pageSize=Math.Clamp(pageSize,1,100);await using var connection=await source.OpenConnectionAsync(cancellationToken);
        await using var count=new NpgsqlCommand("SELECT count(*) FROM admin_audit_log",connection);long total=(long)(await count.ExecuteScalarAsync(cancellationToken))!;
        await using var command=new NpgsqlCommand("SELECT id,actor_subject,action,target_subject,match_id,reason,occurred_at FROM admin_audit_log ORDER BY occurred_at DESC,id LIMIT $1 OFFSET $2",connection);
        command.Parameters.AddWithValue(pageSize);command.Parameters.AddWithValue((page-1)*pageSize);await using var reader=await command.ExecuteReaderAsync(cancellationToken);List<AdminAuditView> items=[];
        while(await reader.ReadAsync(cancellationToken))items.Add(new(reader.GetGuid(0),reader.GetString(1),reader.GetString(2),reader.IsDBNull(3)?null:reader.GetString(3),reader.IsDBNull(4)?null:reader.GetGuid(4),reader.GetString(5),reader.GetFieldValue<DateTimeOffset>(6)));
        return new(items,page,pageSize,total);
    });

    private static async Task InsertAuditAsync(NpgsqlConnection connection,NpgsqlTransaction tx,string actor,string action,string? target,Guid? match,string reason,CancellationToken ct)
    {
        await using var command=new NpgsqlCommand("INSERT INTO admin_audit_log(id,actor_subject,action,target_subject,match_id,reason) VALUES($1,$2,$3,$4,$5,$6)",connection,tx);
        command.Parameters.AddWithValue(Guid.NewGuid());command.Parameters.AddWithValue(actor);command.Parameters.AddWithValue(action);command.Parameters.Add(new NpgsqlParameter{NpgsqlDbType=NpgsqlDbType.Text,Value=(object?)target??DBNull.Value});command.Parameters.Add(new NpgsqlParameter{NpgsqlDbType=NpgsqlDbType.Uuid,Value=(object?)match??DBNull.Value});command.Parameters.AddWithValue(reason);await command.ExecuteNonQueryAsync(ct);
    }
    private async Task<T> GuardAsync<T>(string operation,Func<Task<T>> action){try{return await action();}catch(NpgsqlException exception){logger.LogError("PostgreSQL {Operation} failed; SQLSTATE {SqlState}",operation,exception.SqlState??"connection");throw new PersistenceException(operation,exception.SqlState);}}
}
