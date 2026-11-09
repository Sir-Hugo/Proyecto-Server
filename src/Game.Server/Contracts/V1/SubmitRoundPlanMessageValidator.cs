using Game.Domain;

namespace Game.Server.Contracts.V1;

/// <summary>Validates the wire-level shape of a round-plan message.</summary>
public static class SubmitRoundPlanMessageValidator
{
    // The largest possible energy pool is reached by reserving all energy through round 10:
    // 1 + (9 * 2) = 19. Since ordinary actions cost at least one energy, a plan cannot
    // contain more than 19 action entries.
    public const int MaximumActionCount = 19;

    public static RoundPlanMessageValidationResult Validate(SubmitRoundPlanMessage? message)
    {
        List<MessageValidationIssue> issues = [];
        List<GameAction> actions = [];

        if (message is null)
        {
            issues.Add(new MessageValidationIssue("message_required", "message"));
            return new RoundPlanMessageValidationResult(null, issues);
        }

        if (!string.Equals(message.Type, SubmitRoundPlanMessage.MessageType, StringComparison.Ordinal))
        {
            issues.Add(new MessageValidationIssue("unsupported_message_type", "type"));
        }

        if (message.Version != SubmitRoundPlanMessage.CurrentVersion)
        {
            issues.Add(new MessageValidationIssue("unsupported_message_version", "version"));
        }

        if (message.RequestId == Guid.Empty)
        {
            issues.Add(new MessageValidationIssue("request_id_required", "requestId"));
        }

        if (message.MatchId == Guid.Empty)
        {
            issues.Add(new MessageValidationIssue("match_id_required", "matchId"));
        }

        if (message.ExpectedSequence < 0)
        {
            issues.Add(new MessageValidationIssue("sequence_must_be_nonnegative", "expectedSequence"));
        }

        if (message.Actions is null || message.Actions.Count == 0)
        {
            issues.Add(new MessageValidationIssue("actions_required", "actions"));
        }
        else if (message.Actions.Count > MaximumActionCount)
        {
            issues.Add(new MessageValidationIssue("too_many_actions", "actions"));
        }
        else
        {
            for (int index = 0; index < message.Actions.Count; index++)
            {
                string? actionName = message.Actions[index];
                if (!Enum.TryParse(actionName, ignoreCase: false, out GameAction action) ||
                    !Enum.IsDefined(action))
                {
                    issues.Add(new MessageValidationIssue("unknown_action", $"actions[{index}]"));
                    continue;
                }

                actions.Add(action);
            }
        }

        if (issues.Count > 0)
        {
            return new RoundPlanMessageValidationResult(null, issues);
        }

        return new RoundPlanMessageValidationResult(
            new ValidatedRoundPlanRequest(
                message.RequestId,
                message.MatchId,
                message.ExpectedSequence,
                actions.AsReadOnly()),
            []);
    }
}
