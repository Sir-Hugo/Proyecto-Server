namespace Game.Server.Contracts.V1;

public sealed record JoinMatchMessage(string? Type, int Version, Guid MatchId)
{
    public const string MessageType = "match.join";
}

public sealed record JoinRoomMessage(string? Type, int Version, Guid RoomId)
{
    public const string MessageType = "room.join";
}

public sealed record LeaveRoomMessage(string? Type, int Version, Guid RoomId)
{
    public const string MessageType = "room.leave";
}
