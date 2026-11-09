namespace Game.Server.Contracts.V1;

/// <summary>Versioned request to choose the display name for one match.</summary>
public sealed record SetMatchNicknameMessage(
    string? Type,
    int Version,
    Guid RequestId,
    Guid MatchId,
    string? Nickname)
{
    public const string MessageType = "match.nickname.set";
    public const int CurrentVersion = 1;
}
