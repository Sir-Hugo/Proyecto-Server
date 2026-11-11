using System.Data;
using System.Text.Json;
using Game.Domain;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace Game.Infrastructure;

public sealed class PostgresMatchSessionStore(NpgsqlDataSource source, ILogger<PostgresMatchSessionStore> logger) : IMatchSessionStore
{
    public Task CreateAsync(MatchSession session, CancellationToken cancellationToken = default) => GuardAsync("create live match", async () =>
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var participants = session.Participants.Select(pair => new StoredParticipant(pair.Value, pair.Key,
            session.Nicknames.GetValueOrDefault(pair.Key), (short)(pair.Value == session.Match.FirstPlayer.PlayerId ? 1 : 2))).ToArray();
        await PostgresMatchStore.InsertAsync(connection, transaction,
            new(session.Match, participants, session.CreatedAt, session.CompletedAt), cancellationToken);
        await UpdateRecoveryAsync(connection, transaction, session, cancellationToken);
        await AppendEventsAsync(connection, transaction, session.NewEvents, cancellationToken);
        await AppendNotificationsAsync(connection, transaction, session.NewNotifications, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    });

    public Task<MatchSession?> ReadAsync(Guid matchId, CancellationToken cancellationToken = default) => GuardAsync("read live match", async () =>
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        var session = await LoadAsync(connection, transaction, matchId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return session;
    });

    public Task<T> MutateAsync<T>(Guid matchId, Func<MatchSession, T> operation, CancellationToken cancellationToken = default) => GuardAsync("commit live match", async () =>
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var command = new NpgsqlCommand("SELECT id FROM matches WHERE id=$1 FOR UPDATE", connection, transaction))
        {
            command.Parameters.AddWithValue(matchId);
            if (await command.ExecuteScalarAsync(cancellationToken) is null) throw new MatchOperationException("not_match_participant");
        }
        var session = (await LoadAsync(connection, transaction, matchId, cancellationToken))!;
        var previousState = session.Match;
        var previousStarted = session.StartedAt;
        var previousDeadline = session.RoundDeadlineAt;
        var previousReason = session.TerminalReason;
        var previousNicknames = new Dictionary<string, string>(session.Nicknames);
        var previousPending = new Dictionary<string, PendingRoundPlan>(session.PendingPlans);
        var previousOutcomes = session.Submissions.ToDictionary(pair => pair.Key, pair => pair.Value.Outcome);

        // Synchronous domain work only. No SignalR, HTTP or other network callbacks inside this transaction.
        T result = operation(session);

        foreach (var pair in previousPending.Where(pair => !session.PendingPlans.ContainsKey(pair.Key)))
        {
            await using var delete = new NpgsqlCommand("DELETE FROM pending_round_plans WHERE match_id=$1 AND player_id=$2", connection, transaction);
            delete.Parameters.AddWithValue(matchId);
            delete.Parameters.AddWithValue(session.Participants[pair.Key]);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }
        if (session.Match != previousState || session.StartedAt != previousStarted ||
            session.RoundDeadlineAt != previousDeadline || session.TerminalReason != previousReason)
            await UpdateStateAsync(connection, transaction, session, cancellationToken);
        foreach (var pair in session.Nicknames.Where(pair => previousNicknames.GetValueOrDefault(pair.Key) != pair.Value))
        {
            await using var update = new NpgsqlCommand("UPDATE match_participants SET nickname=$1 WHERE match_id=$2 AND player_id=$3", connection, transaction);
            update.Parameters.AddWithValue(pair.Value);
            update.Parameters.AddWithValue(matchId);
            update.Parameters.AddWithValue(session.Participants[pair.Key]);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
        foreach (var (key, record) in session.Submissions)
        {
            bool inserted = !previousOutcomes.ContainsKey(key);
            if (inserted)
            {
                await using var insert = new NpgsqlCommand("""
                    INSERT INTO match_commands(match_id,player_id,request_id,request_fingerprint,accepted_sequence,acknowledgement)
                    VALUES ($1,$2,$3,$4,$5,$6)
                    """, connection, transaction);
                insert.Parameters.AddWithValue(matchId);
                insert.Parameters.AddWithValue(session.Participants[key.Subject]);
                insert.Parameters.AddWithValue(key.RequestId);
                insert.Parameters.AddWithValue(record.Fingerprint);
                insert.Parameters.AddWithValue(record.Acknowledgement.GetProperty("sequence").GetInt64());
                insert.Parameters.AddWithValue(NpgsqlDbType.Jsonb, record.Acknowledgement.GetRawText());
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }
            if (record.Outcome is not null && (inserted || record.Outcome != previousOutcomes[key]))
            {
                await using var update = new NpgsqlCommand("UPDATE match_commands SET outcome=$1,outcome_event_type=$2 WHERE match_id=$3 AND player_id=$4 AND request_id=$5", connection, transaction);
                update.Parameters.AddWithValue(NpgsqlDbType.Jsonb, record.Outcome.Message.GetRawText());
                update.Parameters.AddWithValue(record.Outcome.EventName);
                update.Parameters.AddWithValue(matchId);
                update.Parameters.AddWithValue(session.Participants[key.Subject]);
                update.Parameters.AddWithValue(key.RequestId);
                await update.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        foreach (var pair in session.PendingPlans.Where(pair => !previousPending.ContainsKey(pair.Key)))
        {
            await using var insert = new NpgsqlCommand("""
                INSERT INTO pending_round_plans(match_id,round_number,player_id,request_id,actions) VALUES ($1,$2,$3,$4,$5)
                """, connection, transaction);
            insert.Parameters.AddWithValue(matchId);
            insert.Parameters.AddWithValue(session.Match.RoundNumber);
            insert.Parameters.AddWithValue(pair.Value.Plan.PlayerId);
            insert.Parameters.AddWithValue(pair.Value.CommandKey.RequestId);
            insert.Parameters.AddWithValue(NpgsqlDbType.Jsonb, JsonSerializer.Serialize(pair.Value.Plan.Actions.Select(action => action.ToString())));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await AppendEventsAsync(connection, transaction, session.NewEvents, cancellationToken);
        await AppendNotificationsAsync(connection, transaction, session.NewNotifications, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    });

    public Task<IReadOnlyList<Guid>> FindExpiredAsync(DateTimeOffset deadline, CancellationToken cancellationToken = default) => GuardAsync<IReadOnlyList<Guid>>("find expired matches", async () =>
    {
        await using var command = source.CreateCommand("SELECT id FROM matches WHERE status='active' AND round_deadline_at <= $1");
        command.Parameters.AddWithValue(deadline.ToUniversalTime());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        List<Guid> ids = [];
        while (await reader.ReadAsync(cancellationToken)) ids.Add(reader.GetGuid(0));
        return ids;
    });

    public Task<bool> IsPlayerActiveAsync(string subject, CancellationToken cancellationToken = default) => GuardAsync("read active assignment", async () =>
    {
        await using var command = source.CreateCommand("SELECT EXISTS(SELECT 1 FROM active_match_players WHERE auth_subject=$1)");
        command.Parameters.AddWithValue(subject);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    });

    public Task<Guid?> FindLatestMatchAsync(string subject, CancellationToken cancellationToken = default) => GuardAsync<Guid?>("find participant match", async () =>
    {
        await using var command = source.CreateCommand("""
            SELECT p.match_id FROM match_participants p JOIN matches m ON m.id=p.match_id
            WHERE p.auth_subject=$1 ORDER BY (m.status='active') DESC, m.created_at DESC LIMIT 1
            """);
        command.Parameters.AddWithValue(subject);
        object? value = await command.ExecuteScalarAsync(cancellationToken);
        return value is Guid id ? id : null;
    });

    public Task<IReadOnlyList<PendingNotification>> ReadPendingNotificationsAsync(Guid? matchId = null, string? subject = null,
        int limit = 100, CancellationToken cancellationToken = default) => GuardAsync<IReadOnlyList<PendingNotification>>("read outbox", async () =>
    {
        await using var command = source.CreateCommand("""
            SELECT id,match_id,target_subject,event_type,sequence_number,payload::text,created_at,attempt_count
            FROM match_outbox WHERE delivered_at IS NULL AND failed_at IS NULL AND next_attempt_at <= CURRENT_TIMESTAMP
              AND ($1::uuid IS NULL OR match_id=$1) AND ($2::text IS NULL OR target_subject=$2)
            ORDER BY created_at,id LIMIT $3
            """);
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType=NpgsqlDbType.Uuid, Value=(object?)matchId ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType=NpgsqlDbType.Text, Value=(object?)subject ?? DBNull.Value });
        command.Parameters.AddWithValue(Math.Clamp(limit, 1, 500));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        List<PendingNotification> result=[];
        while(await reader.ReadAsync(cancellationToken)) result.Add(new(reader.GetGuid(0),reader.GetGuid(1),reader.GetString(2),reader.GetString(3),reader.GetInt64(4),
            JsonSerializer.Deserialize<JsonElement>(reader.GetString(5)),reader.GetFieldValue<DateTimeOffset>(6),reader.GetInt32(7)));
        return result;
    });

    public Task MarkNotificationDeliveredAsync(Guid id, CancellationToken cancellationToken = default) => GuardAsync("complete outbox", async () =>
    {
        await using var command=source.CreateCommand("UPDATE match_outbox SET delivered_at=CURRENT_TIMESTAMP,last_error=NULL WHERE id=$1 AND delivered_at IS NULL");
        command.Parameters.AddWithValue(id); await command.ExecuteNonQueryAsync(cancellationToken); return true;
    });

    public Task MarkNotificationFailedAsync(Guid id,string errorCode,DateTimeOffset nextAttemptAt,CancellationToken cancellationToken=default) => GuardAsync("retry outbox",async()=>
    {
        await using var command=source.CreateCommand("""
            UPDATE match_outbox SET attempt_count=attempt_count+1,last_error=$2,next_attempt_at=$3,
                failed_at=CASE WHEN attempt_count+1 >= 10 THEN CURRENT_TIMESTAMP ELSE NULL END
            WHERE id=$1 AND delivered_at IS NULL AND failed_at IS NULL
            """);
        command.Parameters.AddWithValue(id); command.Parameters.AddWithValue(errorCode); command.Parameters.AddWithValue(nextAttemptAt.ToUniversalTime());
        await command.ExecuteNonQueryAsync(cancellationToken); return true;
    });

    private static async Task<MatchSession?> LoadAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id, CancellationToken ct)
    {
        var stored = await PostgresMatchStore.ReadAsync(connection, transaction, id, ct);
        if (stored is null) return null;
        var session = new MatchSession(stored.State, stored.Participants.ToDictionary(p => p.AuthSubject, p => p.PlayerId))
        { CreatedAt = stored.CreatedAt, CompletedAt = stored.CompletedAt };
        foreach (var participant in stored.Participants.Where(p => p.Nickname is not null)) session.Nicknames[participant.AuthSubject] = participant.Nickname!;
        await using (var command = new NpgsqlCommand("SELECT started_at,round_deadline_at,terminal_reason FROM matches WHERE id=$1", connection, transaction))
        {
            command.Parameters.AddWithValue(id);
            await using var reader=await command.ExecuteReaderAsync(ct); await reader.ReadAsync(ct);
            session.StartedAt = reader.IsDBNull(0) ? null : reader.GetFieldValue<DateTimeOffset>(0);
            session.RoundDeadlineAt = reader.IsDBNull(1) ? null : reader.GetFieldValue<DateTimeOffset>(1);
            session.TerminalReason = reader.IsDBNull(2) ? null : reader.GetString(2);
        }
        await using (var command = new NpgsqlCommand("""
            SELECT p.auth_subject,c.request_id,c.request_fingerprint,c.acknowledgement::text,c.outcome::text,c.outcome_event_type
            FROM match_commands c JOIN match_participants p USING(match_id,player_id) WHERE c.match_id=$1
            """, connection, transaction))
        {
            command.Parameters.AddWithValue(id);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var record = new SubmissionRecord(reader.GetFieldValue<byte[]>(2), JsonSerializer.Deserialize<JsonElement>(reader.GetString(3)));
                if (!reader.IsDBNull(4)) record.Outcome = new(reader.GetString(5), JsonSerializer.Deserialize<JsonElement>(reader.GetString(4)));
                session.Submissions.Add(new(reader.GetString(0), reader.GetGuid(1)), record);
            }
        }
        await using (var command = new NpgsqlCommand("""
            SELECT p.auth_subject,r.player_id,r.request_id,r.actions::text FROM pending_round_plans r
            JOIN match_participants p USING(match_id,player_id) WHERE r.match_id=$1
            """, connection, transaction))
        {
            command.Parameters.AddWithValue(id);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                string subject = reader.GetString(0);
                session.PendingPlans.Add(subject, new(new(reader.GetGuid(1),
                    JsonSerializer.Deserialize<string[]>(reader.GetString(3))!.Select(Enum.Parse<GameAction>).ToArray()), new(subject, reader.GetGuid(2))));
            }
        }
        return session;
    }

    private static async Task UpdateStateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, MatchSession session, CancellationToken ct)
    {
        var m = session.Match;
        await using var command = new NpgsqlCommand("""
            UPDATE matches SET status=$1,round_number=$2,sequence_number=$3,first_health=$4,second_health=$5,
                first_energy=$6,second_energy=$7,first_reserved_energy=$8,second_reserved_energy=$9,
                first_used_blocks=$10,second_used_blocks=$11,first_used_special=$12,second_used_special=$13,
                winner_player_id=$14,last_activity_at=$15,started_at=$16,completed_at=$17,
                round_deadline_at=$18,terminal_reason=$19 WHERE id=$20
            """, connection, transaction);
        command.Parameters.AddWithValue(m.Status == MatchStatus.Active ? "active" : "completed");
        command.Parameters.AddWithValue(m.RoundNumber);
        command.Parameters.AddWithValue(m.SequenceNumber);
        command.Parameters.AddWithValue(m.FirstPlayer.Health);
        command.Parameters.AddWithValue(m.SecondPlayer.Health);
        command.Parameters.AddWithValue(m.FirstPlayer.Energy);
        command.Parameters.AddWithValue(m.SecondPlayer.Energy);
        command.Parameters.AddWithValue(m.FirstPlayer.ReservedEnergy);
        command.Parameters.AddWithValue(m.SecondPlayer.ReservedEnergy);
        command.Parameters.AddWithValue(m.FirstPlayer.UsedBlockades);
        command.Parameters.AddWithValue(m.SecondPlayer.UsedBlockades);
        command.Parameters.AddWithValue(m.FirstPlayer.HasUsedSpecialAttack);
        command.Parameters.AddWithValue(m.SecondPlayer.HasUsedSpecialAttack);
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = (object?)m.WinnerId ?? DBNull.Value });
        command.Parameters.AddWithValue(m.LastActivityAt.ToUniversalTime());
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = (object?)session.StartedAt?.ToUniversalTime() ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = (object?)session.CompletedAt?.ToUniversalTime() ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType=NpgsqlDbType.TimestampTz, Value=(object?)session.RoundDeadlineAt?.ToUniversalTime() ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType=NpgsqlDbType.Text, Value=(object?)session.TerminalReason ?? DBNull.Value });
        command.Parameters.AddWithValue(m.MatchId);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task UpdateRecoveryAsync(NpgsqlConnection connection,NpgsqlTransaction transaction,MatchSession session,CancellationToken ct)
    {
        await using var command=new NpgsqlCommand("UPDATE matches SET round_deadline_at=$1,terminal_reason=$2 WHERE id=$3",connection,transaction);
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType=NpgsqlDbType.TimestampTz, Value=(object?)session.RoundDeadlineAt?.ToUniversalTime() ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType=NpgsqlDbType.Text, Value=(object?)session.TerminalReason ?? DBNull.Value });
        command.Parameters.AddWithValue(session.Match.MatchId); await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task AppendNotificationsAsync(NpgsqlConnection connection,NpgsqlTransaction transaction,IEnumerable<PendingNotification> notifications,CancellationToken ct)
    {
        foreach(var item in notifications)
        {
            await using var command=new NpgsqlCommand("""
                INSERT INTO match_outbox(id,match_id,target_subject,event_type,sequence_number,payload,created_at,next_attempt_at)
                VALUES($1,$2,$3,$4,$5,$6,$7,$7)
                """,connection,transaction);
            command.Parameters.AddWithValue(item.Id);command.Parameters.AddWithValue(item.MatchId);command.Parameters.AddWithValue(item.Subject);
            command.Parameters.AddWithValue(item.EventName);command.Parameters.AddWithValue(item.Sequence);command.Parameters.AddWithValue(NpgsqlDbType.Jsonb,item.Message.GetRawText());
            command.Parameters.AddWithValue(item.CreatedAt.ToUniversalTime());await command.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task AppendEventsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, IEnumerable<StoredEvent> events, CancellationToken ct)
    {
        foreach (var item in events)
        {
            await using var command = new NpgsqlCommand("""
                INSERT INTO match_events(match_id,sequence_number,event_type,event_version,occurred_at,payload) VALUES ($1,$2,$3,$4,$5,$6)
                """, connection, transaction);
            command.Parameters.AddWithValue(item.MatchId);
            command.Parameters.AddWithValue(item.Sequence);
            command.Parameters.AddWithValue(item.Type);
            command.Parameters.AddWithValue(item.Version);
            command.Parameters.AddWithValue(item.OccurredAt.ToUniversalTime());
            command.Parameters.AddWithValue(NpgsqlDbType.Jsonb, item.Payload.GetRawText());
            await command.ExecuteNonQueryAsync(ct);
        }
    }

    private async Task<T> GuardAsync<T>(string operation, Func<Task<T>> action)
    {
        try { return await action(); }
        catch (PostgresException exception) when (exception.ConstraintName == "active_match_players_pkey")
        { throw new MatchOperationException("player_already_in_match"); }
        catch (NpgsqlException exception)
        {
            logger.LogError("PostgreSQL {Operation} failed; SQLSTATE {SqlState}", operation, exception.SqlState ?? "connection");
            throw new PersistenceException(operation, exception.SqlState);
        }
    }
}
