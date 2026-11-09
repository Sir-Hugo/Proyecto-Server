using Game.Domain;

namespace Game.Server.Contracts.V1;

// Structurally validated request ready for authorization and domain checks.
public sealed record ValidatedRoundPlanRequest(
    Guid RequestId,
    Guid MatchId,
    long ExpectedSequence,
    IReadOnlyList<GameAction> Actions);
