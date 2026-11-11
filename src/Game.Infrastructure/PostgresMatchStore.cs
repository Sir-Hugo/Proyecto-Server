using System.Security.Cryptography;
using System.Text.Json;
using Game.Domain;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace Game.Infrastructure;

public sealed record StoredEvent(Guid MatchId, long Sequence, string Type, short Version,
    DateTimeOffset OccurredAt, JsonElement Payload);
public sealed record StoredPendingPlan(Guid MatchId, Guid PlayerId, Guid RequestId, short Round,
    long ExpectedSequence, IReadOnlyList<GameAction> Actions, JsonElement Acknowledgement);

// Low-level infrastructure operations retained from 2A. Live rounds use PostgresMatchSessionStore's single transaction.
public sealed class PostgresMatchStore(NpgsqlDataSource dataSource, ILogger<PostgresMatchStore> logger)
{
    public Task CreateAsync(StoredMatch match, CancellationToken cancellationToken = default) => ExecuteAsync("create match", async () =>
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await InsertAsync(connection, transaction, match, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    });

    internal static async Task InsertAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, StoredMatch match, CancellationToken cancellationToken)
    {
        var m = match.State;
        var first = m.FirstPlayer;
        var second = m.SecondPlayer;
        await using (var command = new NpgsqlCommand("""
            INSERT INTO matches(id, status, round_number, sequence_number, first_player_id, second_player_id,
                first_health, second_health, first_energy, second_energy, first_reserved_energy, second_reserved_energy,
                first_used_blocks, second_used_blocks, first_used_special, second_used_special,
                winner_player_id, last_activity_at, created_at, completed_at)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16,$17,$18,$19,$20)
            """, connection, transaction))
        {
            command.Parameters.AddWithValue(m.MatchId);
            command.Parameters.AddWithValue(m.Status == MatchStatus.Active ? "active" : "completed");
            command.Parameters.AddWithValue(m.RoundNumber);
            command.Parameters.AddWithValue(m.SequenceNumber);
            command.Parameters.AddWithValue(first.PlayerId);
            command.Parameters.AddWithValue(second.PlayerId);
            command.Parameters.AddWithValue(first.Health);
            command.Parameters.AddWithValue(second.Health);
            command.Parameters.AddWithValue(first.Energy);
            command.Parameters.AddWithValue(second.Energy);
            command.Parameters.AddWithValue(first.ReservedEnergy);
            command.Parameters.AddWithValue(second.ReservedEnergy);
            command.Parameters.AddWithValue(first.UsedBlockades);
            command.Parameters.AddWithValue(second.UsedBlockades);
            command.Parameters.AddWithValue(first.HasUsedSpecialAttack);
            command.Parameters.AddWithValue(second.HasUsedSpecialAttack);
            command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = (object?)m.WinnerId ?? DBNull.Value });
            command.Parameters.AddWithValue(m.LastActivityAt.ToUniversalTime());
            command.Parameters.AddWithValue(match.CreatedAt.ToUniversalTime());
            command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = (object?)match.CompletedAt?.ToUniversalTime() ?? DBNull.Value });
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        foreach (var participant in match.Participants)
        {
            await using var command = new NpgsqlCommand("""
                INSERT INTO match_participants(match_id, player_id, auth_subject, nickname, slot)
                VALUES ($1,$2,$3,$4,$5)
                """, connection, transaction);
            command.Parameters.AddWithValue(m.MatchId);
            command.Parameters.AddWithValue(participant.PlayerId);
            command.Parameters.AddWithValue(participant.AuthSubject);
            command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Varchar, Value = (object?)participant.Nickname ?? DBNull.Value });
            command.Parameters.AddWithValue(participant.Slot);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public Task<StoredMatch?> GetAsync(Guid matchId, CancellationToken cancellationToken = default) => ExecuteAsync<StoredMatch?>("read match", async () =>
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await ReadAsync(connection, null, matchId, cancellationToken);
    });

    internal static async Task<StoredMatch?> ReadAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid matchId, CancellationToken cancellationToken)
    {
        // One statement provides a consistent snapshot of state and both participants.
        await using var command = new NpgsqlCommand("""
            SELECT m.id, m.status, m.round_number, m.sequence_number, m.first_player_id, m.second_player_id,
                m.first_health, m.second_health, m.first_energy, m.second_energy, m.first_reserved_energy, m.second_reserved_energy,
                m.first_used_blocks, m.second_used_blocks, m.first_used_special, m.second_used_special,
                m.winner_player_id, m.last_activity_at, m.created_at, m.completed_at,
                p.player_id, p.auth_subject, p.nickname, p.slot
            FROM matches m JOIN match_participants p ON p.match_id = m.id WHERE m.id = $1 ORDER BY p.slot
            """, connection, transaction);
        command.Parameters.AddWithValue(matchId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        MatchState? state = null;
        DateTimeOffset createdAt = default;
        DateTimeOffset? completedAt = null;
        List<StoredParticipant> participants = [];
        while (await reader.ReadAsync(cancellationToken))
        {
            state ??= new MatchState(reader.GetGuid(0), reader.GetInt16(2),
                new(reader.GetGuid(4), reader.GetInt16(6), reader.GetInt32(8), reader.GetInt32(10), reader.GetInt16(12), reader.GetBoolean(14)),
                new(reader.GetGuid(5), reader.GetInt16(7), reader.GetInt32(9), reader.GetInt32(11), reader.GetInt16(13), reader.GetBoolean(15)),
                reader.GetString(1) == "active" ? MatchStatus.Active : MatchStatus.Finished,
                reader.IsDBNull(16) ? null : reader.GetGuid(16), reader.GetInt64(3), reader.GetFieldValue<DateTimeOffset>(17));
            createdAt = reader.GetFieldValue<DateTimeOffset>(18);
            completedAt = reader.IsDBNull(19) ? null : reader.GetFieldValue<DateTimeOffset>(19);
            participants.Add(new(reader.GetGuid(20), reader.GetString(21), reader.IsDBNull(22) ? null : reader.GetString(22), reader.GetInt16(23)));
        }
        return state is null ? null : new(state, participants, createdAt, completedAt);
    }

    public Task AppendEventAsync(StoredEvent matchEvent, CancellationToken cancellationToken = default) => ExecuteAsync("append event", async () =>
    {
        await using var command = dataSource.CreateCommand("""
            INSERT INTO match_events(match_id, sequence_number, event_type, event_version, occurred_at, payload)
            VALUES ($1,$2,$3,$4,$5,$6)
            """);
        command.Parameters.AddWithValue(matchEvent.MatchId);
        command.Parameters.AddWithValue(matchEvent.Sequence);
        command.Parameters.AddWithValue(matchEvent.Type);
        command.Parameters.AddWithValue(matchEvent.Version);
        command.Parameters.AddWithValue(matchEvent.OccurredAt.ToUniversalTime());
        command.Parameters.AddWithValue(NpgsqlDbType.Jsonb, matchEvent.Payload.GetRawText());
        await command.ExecuteNonQueryAsync(cancellationToken);
    });

    public Task SavePendingPlanAsync(StoredPendingPlan plan, CancellationToken cancellationToken = default) => ExecuteAsync("save private plan", async () =>
    {
        // This verifies command/plan storage, not the full round transition of Phase 2B.
        string actions = JsonSerializer.Serialize(plan.Actions.Select(action => action.ToString()));
        byte[] fingerprint = SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { plan.MatchId, plan.ExpectedSequence, Actions = plan.Actions.Select(action => action.ToString()).ToArray() }));
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var command = new NpgsqlCommand("""
            INSERT INTO match_commands(match_id, player_id, request_id, request_fingerprint, accepted_sequence, acknowledgement)
            VALUES ($1,$2,$3,$4,$5,$6)
            """, connection, transaction))
        {
            command.Parameters.AddWithValue(plan.MatchId);
            command.Parameters.AddWithValue(plan.PlayerId);
            command.Parameters.AddWithValue(plan.RequestId);
            command.Parameters.AddWithValue(fingerprint);
            command.Parameters.AddWithValue(plan.ExpectedSequence);
            command.Parameters.AddWithValue(NpgsqlDbType.Jsonb, plan.Acknowledgement.GetRawText());
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var command = new NpgsqlCommand("""
            INSERT INTO pending_round_plans(match_id, round_number, player_id, request_id, actions)
            VALUES ($1,$2,$3,$4,$5)
            """, connection, transaction))
        {
            command.Parameters.AddWithValue(plan.MatchId);
            command.Parameters.AddWithValue(plan.Round);
            command.Parameters.AddWithValue(plan.PlayerId);
            command.Parameters.AddWithValue(plan.RequestId);
            command.Parameters.AddWithValue(NpgsqlDbType.Jsonb, actions);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    });

    public Task<StoredPendingPlan?> GetPendingPlanAsync(Guid matchId, Guid playerId, CancellationToken cancellationToken = default) => ExecuteAsync<StoredPendingPlan?>("read private plan", async () =>
    {
        // Caller must authorize the requesting subject; only a single player's plan is returned.
        await using var command = dataSource.CreateCommand("""
            SELECT p.request_id, p.round_number, c.accepted_sequence, p.actions::text, c.acknowledgement::text
            FROM pending_round_plans p JOIN match_commands c USING (match_id, player_id, request_id)
            WHERE p.match_id = $1 AND p.player_id = $2
            """);
        command.Parameters.AddWithValue(matchId);
        command.Parameters.AddWithValue(playerId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new(matchId, playerId, reader.GetGuid(0), reader.GetInt16(1), reader.GetInt64(2),
            JsonSerializer.Deserialize<string[]>(reader.GetString(3))!.Select(Enum.Parse<GameAction>).ToArray(),
            JsonSerializer.Deserialize<JsonElement>(reader.GetString(4)));
    });

    private async Task ExecuteAsync(string operation, Func<Task> action)
    {
        await ExecuteAsync(operation, async () => { await action(); return true; });
    }

    private async Task<T> ExecuteAsync<T>(string operation, Func<Task<T>> action)
    {
        try { return await action(); }
        catch (NpgsqlException exception)
        {
            logger.LogError("PostgreSQL {Operation} failed; SQLSTATE {SqlState}", operation, exception.SqlState ?? "connection");
            throw new PersistenceException(operation, exception.SqlState);
        }
    }
}
