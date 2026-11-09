namespace Game.Server.Contracts.V1;

// Player intent for all actions in one simultaneous round.
public sealed record SubmitRoundPlanMessage(
    string? Type,
    int Version,
    Guid RequestId,
    Guid MatchId,
    long ExpectedSequence,
    IReadOnlyList<string>? Actions)
{
    public const string MessageType = "round.plan.submit";
    public const int CurrentVersion = 1;
}
