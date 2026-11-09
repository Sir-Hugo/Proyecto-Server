using System.Security.Cryptography;
using System.Text;
using Game.Domain;

namespace Game.Server.Rooms;

/// <summary>
/// Local, single-process room storage for the initial lobby implementation.
/// Replace with durable storage before running multiple server instances.
/// </summary>
public sealed class InMemoryRoomService(MatchRoundService roundService)
{
    private const int RoomCapacity = 2;
    private const int InviteCodeLength = 12;
    private const string InviteCodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private readonly object gate = new();
    private readonly Dictionary<Guid, RoomState> rooms = [];

    public RoomOperationResult<CreatedRoomResponse> Create(string playerSubject, string? visibility)
    {
        string? normalizedVisibility = visibility?.Trim().ToLowerInvariant();
        if (normalizedVisibility is not ("public" or "private"))
        {
            return RoomOperationResult<CreatedRoomResponse>.Failure(400, "invalid_visibility");
        }

        lock (gate)
        {
            Guid roomId = Guid.NewGuid();
            string? inviteCode = normalizedVisibility == "private" ? CreateUniqueInviteCode() : null;
            DateTimeOffset createdAt = DateTimeOffset.UtcNow;
            RoomState room = new(roomId, normalizedVisibility, inviteCode, createdAt);
            room.Players.Add(new RoomPlayerState(playerSubject, Guid.NewGuid(), isOwner: true));
            rooms.Add(roomId, room);

            return RoomOperationResult<CreatedRoomResponse>.Success(
                new CreatedRoomResponse(ToResponse(room, playerSubject), inviteCode));
        }
    }

    public IReadOnlyList<RoomResponse> ListPublicRooms(string viewerSubject)
    {
        lock (gate)
        {
            return rooms.Values
                .Where(room => room.Visibility == "public" && room.Match is null && room.Players.Count < RoomCapacity)
                .OrderBy(room => room.CreatedAt)
                .Select(room => ToResponse(room, viewerSubject))
                .ToArray();
        }
    }

    public RoomOperationResult<RoomResponse> Get(Guid roomId, string viewerSubject)
    {
        lock (gate)
        {
            if (!rooms.TryGetValue(roomId, out RoomState? room))
            {
                return RoomOperationResult<RoomResponse>.Failure(404, "room_not_found");
            }

            bool isMember = room.Players.Any(player => player.Subject == viewerSubject);
            if (room.Visibility == "private" && !isMember)
            {
                return RoomOperationResult<RoomResponse>.Failure(404, "room_not_found");
            }

            if (isMember)
            {
                room.LastActivityAt = DateTimeOffset.UtcNow;
            }

            return RoomOperationResult<RoomResponse>.Success(ToResponse(room, viewerSubject));
        }
    }

    public RoomOperationResult<RoomResponse> Join(Guid roomId, string playerSubject, string? inviteCode)
    {
        lock (gate)
        {
            if (!rooms.TryGetValue(roomId, out RoomState? room))
            {
                return RoomOperationResult<RoomResponse>.Failure(404, "room_not_found");
            }

            if (room.Match is not null)
            {
                return RoomOperationResult<RoomResponse>.Failure(409, "match_already_started");
            }

            RoomPlayerState? existingPlayer = room.Players.FirstOrDefault(player => player.Subject == playerSubject);
            if (existingPlayer is not null)
            {
                room.LastActivityAt = DateTimeOffset.UtcNow;
                return RoomOperationResult<RoomResponse>.Success(ToResponse(room, playerSubject));
            }

            if (room.Visibility == "private" && !InviteCodesMatch(room.InviteCode, inviteCode))
            {
                return RoomOperationResult<RoomResponse>.Failure(404, "room_not_found");
            }

            if (room.Players.Count >= RoomCapacity)
            {
                return RoomOperationResult<RoomResponse>.Failure(409, "room_full");
            }

            room.Players.Add(new RoomPlayerState(playerSubject, Guid.NewGuid(), isOwner: false));
            room.LastActivityAt = DateTimeOffset.UtcNow;
            return RoomOperationResult<RoomResponse>.Success(ToResponse(room, playerSubject));
        }
    }

    public RoomOperationResult<LeaveRoomResponse> Leave(Guid roomId, string playerSubject)
    {
        lock (gate)
        {
            if (!rooms.TryGetValue(roomId, out RoomState? room))
            {
                return RoomOperationResult<LeaveRoomResponse>.Failure(404, "room_not_found");
            }

            bool matchFinished = room.Match is not null && roundService.GetMatchStatus(room.Match.MatchId) == MatchStatus.Finished;
            if (room.Match is not null && !matchFinished)
            {
                return RoomOperationResult<LeaveRoomResponse>.Failure(409, "match_already_started");
            }

            RoomPlayerState? player = room.Players.FirstOrDefault(candidate => candidate.Subject == playerSubject);
            if (player is null)
            {
                return RoomOperationResult<LeaveRoomResponse>.Failure(404, "room_membership_not_found");
            }

            if (room.Match is not null && matchFinished)
            {
                room.Players.Remove(player);
                room.LastActivityAt = DateTimeOffset.UtcNow;
                if (room.Players.Count == 0)
                {
                    rooms.Remove(roomId);
                    return RoomOperationResult<LeaveRoomResponse>.Success(
                        new LeaveRoomResponse(roomId, RoomClosed: true, Status: "closed"));
                }

                if (player.IsOwner)
                    room.Players[0].IsOwner = true;

                return RoomOperationResult<LeaveRoomResponse>.Success(
                    new LeaveRoomResponse(roomId, RoomClosed: false, Status: "Partida finalizada"));
            }

            if (player.IsOwner)
            {
                rooms.Remove(roomId);
                return RoomOperationResult<LeaveRoomResponse>.Success(
                    new LeaveRoomResponse(roomId, RoomClosed: true, Status: "closed"));
            }

            room.Players.Remove(player);
            room.LastActivityAt = DateTimeOffset.UtcNow;
            foreach (RoomPlayerState remainingPlayer in room.Players)
            {
                remainingPlayer.IsReady = false;
            }

            return RoomOperationResult<LeaveRoomResponse>.Success(
                new LeaveRoomResponse(roomId, RoomClosed: false, GetStatus(room)));
        }
    }

    public RoomOperationResult<RoomResponse> SetReady(
        Guid roomId,
        string playerSubject,
        bool isReady)
    {
        lock (gate)
        {
            if (!rooms.TryGetValue(roomId, out RoomState? room))
            {
                return RoomOperationResult<RoomResponse>.Failure(404, "room_not_found");
            }

            if (room.Match is not null)
            {
                return RoomOperationResult<RoomResponse>.Failure(409, "match_already_started");
            }

            RoomPlayerState? player = room.Players.FirstOrDefault(candidate => candidate.Subject == playerSubject);
            if (player is null)
            {
                return RoomOperationResult<RoomResponse>.Failure(403, "room_membership_required");
            }

            player.IsReady = isReady;
            room.LastActivityAt = DateTimeOffset.UtcNow;
            if (room.Players.Count == RoomCapacity && room.Players.All(candidate => candidate.IsReady))
            {
                room.Match = MatchState.Create(
                    Guid.NewGuid(),
                    room.Players[0].PlayerId,
                    room.Players[1].PlayerId,
                    room.LastActivityAt);
                roundService.Register(room.Match, room.Players.ToDictionary(player => player.Subject, player => player.PlayerId));
            }

            return RoomOperationResult<RoomResponse>.Success(ToResponse(room, playerSubject));
        }
    }

    public int RemoveInactiveRooms(DateTimeOffset now, TimeSpan inactivityLimit)
    {
        if (inactivityLimit <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(inactivityLimit));
        }

        lock (gate)
        {
            Guid[] expiredRoomIds = rooms.Values
                .Where(room => room.Match is null && now - room.LastActivityAt >= inactivityLimit)
                .Select(room => room.Id)
                .ToArray();

            foreach (Guid roomId in expiredRoomIds)
            {
                rooms.Remove(roomId);
            }

            return expiredRoomIds.Length;
        }
    }

    public bool TryGetMatchParticipant(
        Guid matchId,
        string subject,
        out MatchState? match,
        out Guid playerId)
    {
        lock (gate)
        {
            RoomState? room = rooms.Values.FirstOrDefault(candidate => candidate.Match?.MatchId == matchId);
            RoomPlayerState? player = room?.Players.FirstOrDefault(candidate => candidate.Subject == subject);
            match = room?.Match;
            playerId = player?.PlayerId ?? Guid.Empty;
            return match is not null && player is not null;
        }
    }

    public bool IsMember(Guid roomId, string playerSubject)
    {
        lock (gate)
        {
            return rooms.TryGetValue(roomId, out RoomState? room) &&
                   room.Players.Any(player => player.Subject == playerSubject);
        }
    }

    private string CreateUniqueInviteCode()
    {
        string code;
        do
        {
            char[] characters = new char[InviteCodeLength];
            for (int index = 0; index < characters.Length; index++)
            {
                characters[index] = InviteCodeAlphabet[RandomNumberGenerator.GetInt32(InviteCodeAlphabet.Length)];
            }

            code = new string(characters);
        }
        while (rooms.Values.Any(room => room.InviteCode == code));

        return code;
    }

    private static bool InviteCodesMatch(string? expected, string? provided)
    {
        if (expected is null || provided is null)
        {
            return false;
        }

        byte[] expectedBytes = Encoding.ASCII.GetBytes(expected);
        byte[] providedBytes = Encoding.ASCII.GetBytes(provided.Trim().ToUpperInvariant());
        return expectedBytes.Length == providedBytes.Length &&
               CryptographicOperations.FixedTimeEquals(expectedBytes, providedBytes);
    }

    private RoomResponse ToResponse(RoomState room, string viewerSubject)
    {
        RoomPlayerState? viewer = room.Players.FirstOrDefault(player => player.Subject == viewerSubject);
        RoomPlayerResponse[] players = room.Players
            .Select(player => new RoomPlayerResponse(player.PlayerId, player.IsOwner, player.IsReady))
            .ToArray();

        return new RoomResponse(
            room.Id,
            room.Visibility,
            GetStatus(room),
            room.Players.Count,
            RoomCapacity,
            room.CreatedAt,
            viewer?.IsOwner ?? false,
            viewer?.IsReady ?? false,
            players,
            room.Match?.MatchId);
    }

    private string GetStatus(RoomState room)
    {
        if (room.Match is not null)
        {
            return roundService.GetMatchStatus(room.Match.MatchId) == MatchStatus.Finished
                ? "Partida finalizada"
                : "Partida en curso";
        }

        if (room.Players.Count < RoomCapacity)
        {
            return "Esperando jugadores";
        }

        return room.Players.All(player => player.IsReady)
            ? "Jugadores listos"
            : "Esperando confirmación";
    }

    private sealed class RoomState(
        Guid id,
        string visibility,
        string? inviteCode,
        DateTimeOffset createdAt)
    {
        public Guid Id { get; } = id;
        public string Visibility { get; } = visibility;
        public string? InviteCode { get; } = inviteCode;
        public DateTimeOffset CreatedAt { get; } = createdAt;
        public DateTimeOffset LastActivityAt { get; set; } = createdAt;
        public List<RoomPlayerState> Players { get; } = [];
        public MatchState? Match { get; set; }
    }

    private sealed class RoomPlayerState(string subject, Guid playerId, bool isOwner)
    {
        public string Subject { get; } = subject;
        public Guid PlayerId { get; } = playerId;
        public bool IsOwner { get; set; } = isOwner;
        public bool IsReady { get; set; }
    }
}
