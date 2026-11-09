namespace Game.Server.Contracts.V1;

public sealed record RoundPlanMessageValidationResult(
    ValidatedRoundPlanRequest? Request,
    IReadOnlyList<MessageValidationIssue> Issues)
{
    public bool IsValid => Request is not null;
}
