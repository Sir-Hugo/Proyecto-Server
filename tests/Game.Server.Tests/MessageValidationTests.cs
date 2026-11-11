using Game.Domain;
using Game.Server.Contracts.V1;

namespace Game.Server.Tests;

public sealed class MessageValidationTests
{
    private static SubmitRoundPlanMessage Valid() => new("round.plan.submit", 1, Guid.NewGuid(), Guid.NewGuid(), 0, ["Attack", "Block", "Reserve", "SpecialAttack"]);
    [Fact] public void AcceptsValidEnvelopeAndMapsActions()
    { var r = SubmitRoundPlanMessageValidator.Validate(Valid()); Assert.True(r.IsValid); Assert.Equal(new[] { GameAction.Attack, GameAction.Block, GameAction.Reserve, GameAction.SpecialAttack }, r.Request!.Actions); }
    [Theory]
    [InlineData("null", "message_required")] [InlineData("type", "unsupported_message_type")] [InlineData("version", "unsupported_message_version")]
    [InlineData("request", "request_id_required")] [InlineData("match", "match_id_required")] [InlineData("sequence", "sequence_must_be_nonnegative")]
    [InlineData("missing", "actions_required")] [InlineData("empty", "actions_required")] [InlineData("many", "too_many_actions")]
    [InlineData("unknown", "unknown_action")] [InlineData("case", "unknown_action")] [InlineData("numeric", "unknown_action")]
    public void RejectsInvalidStructure(string field, string code)
    {
        var m = Valid();
        SubmitRoundPlanMessage? invalid = field switch {
            "null" => null, "type" => m with { Type = "other" }, "version" => m with { Version = 2 },
            "request" => m with { RequestId = Guid.Empty }, "match" => m with { MatchId = Guid.Empty }, "sequence" => m with { ExpectedSequence = -1 },
            "missing" => m with { Actions = null }, "empty" => m with { Actions = [] }, "many" => m with { Actions = Enumerable.Repeat("Attack", 20).ToArray() },
            "case" => m with { Actions = ["attack"] }, "numeric" => m with { Actions = ["0"] }, _ => m with { Actions = ["Unknown"] }
        };
        var r = SubmitRoundPlanMessageValidator.Validate(invalid); Assert.False(r.IsValid); Assert.Null(r.Request); Assert.Contains(r.Issues, i => i.Code == code);
    }
    [Fact] public void AllowsMaximumActionCount() => Assert.True(SubmitRoundPlanMessageValidator.Validate(Valid() with { Actions = Enumerable.Repeat("Reserve", 19).ToArray() }).IsValid);
}
