namespace Game.Server.Rooms;

public sealed record CreateRoomRequest(string? Visibility);

public sealed record JoinRoomRequest(string? InviteCode);

public sealed record SetRoomReadyRequest(bool IsReady);

public sealed record RoomPlayerResponse(Guid PlayerId, bool IsOwner, bool IsReady);

public sealed record RoomResponse(
    Guid RoomId,
    string Visibility,
    string Status,
    int PlayerCount,
    int Capacity,
    DateTimeOffset CreatedAt,
    bool IsOwner,
    bool IsReady,
    IReadOnlyList<RoomPlayerResponse> Players,
    Guid? MatchId);

public sealed record CreatedRoomResponse(RoomResponse Room, string? InviteCode);

public sealed record LeaveRoomResponse(Guid RoomId, bool RoomClosed, string Status);

public sealed record RoomOperationResult<T>(T? Value, int? ErrorStatusCode, string? ErrorCode)
{
    public bool IsSuccess => ErrorStatusCode is null;

    public static RoomOperationResult<T> Success(T value) => new(value, null, null);

    public static RoomOperationResult<T> Failure(int statusCode, string code) =>
        new(default, statusCode, code);
}
