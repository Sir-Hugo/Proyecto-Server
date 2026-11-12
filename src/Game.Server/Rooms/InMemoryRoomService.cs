using System.Security.Cryptography;
using System.Text;
using Game.Domain;
using Game.Infrastructure;
using Npgsql;

namespace Game.Server.Rooms;

// Historical name retained for contract compatibility; storage is PostgreSQL-backed since phase 2C.
public sealed class InMemoryRoomService(MatchRoundService rounds, NpgsqlDataSource source)
{
    private const int Capacity = 2;
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public RoomOperationResult<CreatedRoomResponse> Create(string subject, string? visibility)
    {
        string? kind = visibility?.Trim().ToLowerInvariant();
        if (kind is not ("public" or "private")) return RoomOperationResult<CreatedRoomResponse>.Failure(400, "invalid_visibility");
        try
        {
            using var connection = source.OpenConnection(); using var tx = connection.BeginTransaction();
            Guid id = Guid.NewGuid(), player = Guid.NewGuid(); DateTimeOffset now = Now(); string? code = kind == "private" ? Invite() : null;
            using (var command = new NpgsqlCommand("INSERT INTO rooms(id,visibility,invite_code,status,created_at,last_activity_at) VALUES($1,$2,$3,'waiting',$4,$4)", connection, tx))
            { command.Parameters.AddWithValue(id); command.Parameters.AddWithValue(kind); command.Parameters.AddWithValue((object?)code ?? DBNull.Value); command.Parameters.AddWithValue(now); command.ExecuteNonQuery(); }
            using (var command = new NpgsqlCommand("INSERT INTO room_members(room_id,auth_subject,player_id,is_owner,joined_at) VALUES($1,$2,$3,true,$4)", connection, tx))
            { command.Parameters.AddWithValue(id); command.Parameters.AddWithValue(subject); command.Parameters.AddWithValue(player); command.Parameters.AddWithValue(now); command.ExecuteNonQuery(); }
            tx.Commit(); return RoomOperationResult<CreatedRoomResponse>.Success(new(ToResponse(Load(id)!, subject), code));
        }
        catch (NpgsqlException) { return RoomOperationResult<CreatedRoomResponse>.Failure(503, "database_unavailable"); }
    }

    public IReadOnlyList<RoomResponse> ListPublicRooms(string viewer)
    {
        try
        {
            using var command = source.CreateCommand("SELECT id FROM rooms r WHERE visibility='public' AND status='waiting' AND (SELECT count(*) FROM room_members WHERE room_id=r.id)<2 ORDER BY created_at");
            using var reader = command.ExecuteReader(); List<Guid> ids = []; while (reader.Read()) ids.Add(reader.GetGuid(0)); reader.Close();
            return ids.Select(id => ToResponse(Load(id)!, viewer)).ToArray();
        }
        catch (NpgsqlException) { return []; }
    }

    public async Task<RoomOperationResult<RoomResponse>> GetAsync(Guid id, string viewer, CancellationToken ct = default)
    {
        try
        {
            RoomData? room = Load(id);
            if (room is null || room.DbStatus == "closed" || room.Visibility == "private" && !room.Members.Any(m => m.Subject == viewer)) return RoomOperationResult<RoomResponse>.Failure(404, "room_not_found");
            if (room.Members.Any(m => m.Subject == viewer)) Touch(id);
            string status = room.MatchId is Guid match ? (await rounds.GetMatchStatusAsync(match, ct) == MatchStatus.Finished ? "Partida finalizada" : "Partida en curso") : Status(room);
            return RoomOperationResult<RoomResponse>.Success(ToResponse(room, viewer) with { Status = status });
        }
        catch (PersistenceException) { return RoomOperationResult<RoomResponse>.Failure(503, "database_unavailable"); }
        catch (NpgsqlException) { return RoomOperationResult<RoomResponse>.Failure(503, "database_unavailable"); }
    }

    public RoomOperationResult<RoomResponse> Join(Guid id, string subject, string? invite)
    {
        try
        {
            using var connection = source.OpenConnection(); using var tx = connection.BeginTransaction(); RoomData? room = Load(id, connection, tx, true);
            if (room is null || room.DbStatus == "closed") return RoomOperationResult<RoomResponse>.Failure(404, "room_not_found");
            if (room.MatchId is not null) return RoomOperationResult<RoomResponse>.Failure(409, "match_already_started");
            if (room.Members.Any(m => m.Subject == subject)) { Touch(id, connection, tx); tx.Commit(); return RoomOperationResult<RoomResponse>.Success(ToResponse(Load(id)!, subject)); }
            if (room.Visibility == "private" && !CodesMatch(room.InviteCode, invite)) return RoomOperationResult<RoomResponse>.Failure(404, "room_not_found");
            if (room.Members.Count >= Capacity) return RoomOperationResult<RoomResponse>.Failure(409, "room_full");
            using var command = new NpgsqlCommand("INSERT INTO room_members(room_id,auth_subject,player_id,is_owner,joined_at) VALUES($1,$2,$3,false,$4)", connection, tx);
            command.Parameters.AddWithValue(id); command.Parameters.AddWithValue(subject); command.Parameters.AddWithValue(Guid.NewGuid()); command.Parameters.AddWithValue(Now()); command.ExecuteNonQuery(); Touch(id, connection, tx); tx.Commit();
            return RoomOperationResult<RoomResponse>.Success(ToResponse(Load(id)!, subject));
        }
        catch (NpgsqlException) { return RoomOperationResult<RoomResponse>.Failure(503, "database_unavailable"); }
    }

    public async Task<RoomOperationResult<RoomResponse>> SetReadyAsync(Guid id, string subject, bool ready, CancellationToken ct = default)
    {
        try
        {
            await using var connection = await source.OpenConnectionAsync(ct); await using var tx = await connection.BeginTransactionAsync(ct); RoomData? room = Load(id, connection, tx, true);
            if (room is null || room.DbStatus == "closed") return RoomOperationResult<RoomResponse>.Failure(404, "room_not_found");
            if (!room.Members.Any(m => m.Subject == subject)) return RoomOperationResult<RoomResponse>.Failure(403, "room_membership_required");
            // A retried ready=true may arrive after another concurrent request committed the match.
            // Return the durable room/match assignment so the operation remains idempotent.
            if (room.MatchId is not null)
                return ready
                    ? RoomOperationResult<RoomResponse>.Success(ToResponse(room, subject))
                    : RoomOperationResult<RoomResponse>.Failure(409, "match_already_started");
            await using (var update = new NpgsqlCommand("UPDATE room_members SET is_ready=$1 WHERE room_id=$2 AND auth_subject=$3", connection, tx))
            { update.Parameters.AddWithValue(ready); update.Parameters.AddWithValue(id); update.Parameters.AddWithValue(subject); await update.ExecuteNonQueryAsync(ct); }
            Touch(id, connection, tx); await tx.CommitAsync(ct);
            await using var startConnection = await source.OpenConnectionAsync(ct);
            await using var startTransaction = await startConnection.BeginTransactionAsync(ct);
            await using (var gate = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended($1,0))", startConnection, startTransaction))
            { gate.Parameters.AddWithValue(id.ToString("D")); await gate.ExecuteNonQueryAsync(ct); }
            room = Load(id, startConnection, startTransaction, true);
            if (room!.MatchId is null && room.Members.Count == Capacity && room.Members.All(m => m.Ready))
            {
                var state = MatchState.Create(Guid.NewGuid(), room.Members[0].PlayerId, room.Members[1].PlayerId, Now());
                try { await rounds.CreateAsync(state, room.Members.ToDictionary(m => m.Subject, m => m.PlayerId), ct); }
                catch (MatchOperationException ex) { return RoomOperationResult<RoomResponse>.Failure(409, ex.Code); }
                await using var associate = new NpgsqlCommand("UPDATE rooms SET match_id=$1,status='active',last_activity_at=$2 WHERE id=$3 AND match_id IS NULL",startConnection,startTransaction);
                associate.Parameters.AddWithValue(state.MatchId); associate.Parameters.AddWithValue(Now()); associate.Parameters.AddWithValue(id);
                if (await associate.ExecuteNonQueryAsync(ct) != 1) return RoomOperationResult<RoomResponse>.Failure(409, "match_already_started");
            }
            await startTransaction.CommitAsync(ct);
            return RoomOperationResult<RoomResponse>.Success(ToResponse(Load(id)!, subject));
        }
        catch (PersistenceException) { return RoomOperationResult<RoomResponse>.Failure(503, "database_unavailable"); }
        catch (NpgsqlException) { return RoomOperationResult<RoomResponse>.Failure(503, "database_unavailable"); }
    }

    public async Task<RoomOperationResult<LeaveRoomResponse>> LeaveAsync(Guid id, string subject, CancellationToken ct = default)
    {
        try
        {
            RoomData? room = Load(id); if (room is null || room.DbStatus == "closed") return RoomOperationResult<LeaveRoomResponse>.Failure(404, "room_not_found");
            var member = room.Members.SingleOrDefault(m => m.Subject == subject); if (member is null) return RoomOperationResult<LeaveRoomResponse>.Failure(404, "room_membership_not_found");
            if (room.MatchId is Guid match && await rounds.GetMatchStatusAsync(match, ct) != MatchStatus.Finished) return RoomOperationResult<LeaveRoomResponse>.Failure(409, "match_already_started");
            await using var connection = await source.OpenConnectionAsync(ct); await using var tx = await connection.BeginTransactionAsync(ct);
            await using (var delete = new NpgsqlCommand("DELETE FROM room_members WHERE room_id=$1 AND auth_subject=$2", connection, tx)) { delete.Parameters.AddWithValue(id); delete.Parameters.AddWithValue(subject); await delete.ExecuteNonQueryAsync(ct); }
            int left; await using (var count = new NpgsqlCommand("SELECT count(*) FROM room_members WHERE room_id=$1", connection, tx)) { count.Parameters.AddWithValue(id); left = Convert.ToInt32(await count.ExecuteScalarAsync(ct)); }
            if (member.Owner || left == 0) { await using var close = new NpgsqlCommand("UPDATE rooms SET status='closed',last_activity_at=$1 WHERE id=$2", connection, tx); close.Parameters.AddWithValue(Now()); close.Parameters.AddWithValue(id); await close.ExecuteNonQueryAsync(ct); }
            else { await using var reset = new NpgsqlCommand("UPDATE room_members SET is_ready=false WHERE room_id=$1", connection, tx); reset.Parameters.AddWithValue(id); await reset.ExecuteNonQueryAsync(ct); }
            await tx.CommitAsync(ct); bool closed = member.Owner || left == 0; return RoomOperationResult<LeaveRoomResponse>.Success(new(id, closed, closed ? "closed" : room.MatchId is null ? "Esperando jugadores" : "Partida finalizada"));
        }
        catch (NpgsqlException) { return RoomOperationResult<LeaveRoomResponse>.Failure(503, "database_unavailable"); }
    }

    public int RemoveInactiveRooms(DateTimeOffset now, TimeSpan limit)
    {
        if (limit <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(limit));
        try { using var command = source.CreateCommand("UPDATE rooms SET status='closed' WHERE status='waiting' AND last_activity_at <= $1"); command.Parameters.AddWithValue(now - limit); return command.ExecuteNonQuery(); }
        catch (NpgsqlException) { return 0; }
    }

    public bool IsMember(Guid id, string subject)
    {
        try { using var command = source.CreateCommand("SELECT EXISTS(SELECT 1 FROM room_members m JOIN rooms r ON r.id=m.room_id WHERE m.room_id=$1 AND m.auth_subject=$2 AND r.status<>'closed')"); command.Parameters.AddWithValue(id); command.Parameters.AddWithValue(subject); return (bool)command.ExecuteScalar()!; }
        catch (NpgsqlException) { return false; }
    }

    private RoomData? Load(Guid id, NpgsqlConnection? connection = null, NpgsqlTransaction? tx = null, bool locked = false)
    {
        bool own = connection is null; connection ??= source.OpenConnection();
        try
        {
            using var command = new NpgsqlCommand($"SELECT visibility,invite_code,status,match_id,created_at,last_activity_at FROM rooms WHERE id=$1{(locked ? " FOR UPDATE" : "")}", connection, tx); command.Parameters.AddWithValue(id); using var reader = command.ExecuteReader(); if (!reader.Read()) return null;
            var room = new RoomData(id, reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetGuid(3), reader.GetFieldValue<DateTimeOffset>(4), reader.GetFieldValue<DateTimeOffset>(5), []); reader.Close();
            using var members = new NpgsqlCommand("SELECT auth_subject,player_id,is_owner,is_ready FROM room_members WHERE room_id=$1 ORDER BY joined_at", connection, tx); members.Parameters.AddWithValue(id); using var rows = members.ExecuteReader(); while (rows.Read()) room.Members.Add(new(rows.GetString(0), rows.GetGuid(1), rows.GetBoolean(2), rows.GetBoolean(3))); return room;
        }
        finally { if (own) connection.Dispose(); }
    }
    private static RoomResponse ToResponse(RoomData room, string viewer) { var me = room.Members.FirstOrDefault(m => m.Subject == viewer); return new(room.Id, room.Visibility, Status(room), room.Members.Count, Capacity, room.CreatedAt, me?.Owner ?? false, me?.Ready ?? false, room.Members.Select(m => new RoomPlayerResponse(m.PlayerId, m.Owner, m.Ready)).ToArray(), room.MatchId); }
    private static string Status(RoomData r) => r.MatchId is not null ? "Partida en curso" : r.Members.Count < 2 ? "Esperando jugadores" : r.Members.All(m => m.Ready) ? "Jugadores listos" : "Esperando confirmación";
    private static DateTimeOffset Now() { long ticks = DateTimeOffset.UtcNow.UtcTicks; return new(ticks - ticks % 10, TimeSpan.Zero); }
    private void Touch(Guid id, NpgsqlConnection? connection = null, NpgsqlTransaction? tx = null) { bool own = connection is null; connection ??= source.OpenConnection(); try { using var command = new NpgsqlCommand("UPDATE rooms SET last_activity_at=$1 WHERE id=$2", connection, tx); command.Parameters.AddWithValue(Now()); command.Parameters.AddWithValue(id); command.ExecuteNonQuery(); } finally { if (own) connection.Dispose(); } }
    private static string Invite() { Span<char> chars = stackalloc char[12]; for (int i = 0; i < chars.Length; i++) chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]; return new(chars); }
    private static bool CodesMatch(string? expected, string? provided) { if (expected is null || provided is null) return false; byte[] a = Encoding.ASCII.GetBytes(expected), b = Encoding.ASCII.GetBytes(provided.Trim().ToUpperInvariant()); return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b); }
    private sealed record Member(string Subject, Guid PlayerId, bool Owner, bool Ready);
    private sealed record RoomData(Guid Id, string Visibility, string? InviteCode, string DbStatus, Guid? MatchId, DateTimeOffset CreatedAt, DateTimeOffset LastActivityAt, List<Member> Members);
}
