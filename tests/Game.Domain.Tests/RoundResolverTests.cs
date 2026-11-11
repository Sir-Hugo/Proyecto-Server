using Game.Domain;

namespace Game.Domain.Tests;

public sealed class RoundResolverTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-10T12:00:00Z");
    private static MatchState Initial() => MatchState.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Now);
    private static RoundResolution Resolve(MatchState match, GameAction[] first, GameAction[] second, int roll = 1000) =>
        new RoundResolver(new FixedRoll(roll)).Resolve(match, new(match.FirstPlayer.PlayerId, first), new(match.SecondPlayer.PlayerId, second), Now.AddSeconds(1));
    private sealed class FixedRoll(int roll) : ICriticalHitSource { public int NextBasisPointRoll() => roll; }

    [Fact] public void InitialState()
    {
        var m = Initial();
        Assert.Equal(1, m.RoundNumber); Assert.Equal(0, m.SequenceNumber); Assert.Equal(Now, m.LastActivityAt);
        Assert.Equal(MatchStatus.Active, m.Status); Assert.Null(m.WinnerId);
        foreach (var p in new[] {m.FirstPlayer, m.SecondPlayer})
        { Assert.Equal(50, p.Health); Assert.Equal(1, p.Energy); Assert.Equal(0, p.ReservedEnergy); Assert.Equal(0, p.UsedBlockades); Assert.False(p.HasUsedSpecialAttack); }
    }
    [Theory] [InlineData(0)] [InlineData(2)]
    public void MustSpendExactlyAllEnergy(int count) => Assert.Throws<InvalidOperationException>(() => Resolve(Initial(), Enumerable.Repeat(GameAction.Attack, count).ToArray(), [GameAction.Reserve]));
    [Fact] public void EnergyRecoversBetweenRounds()
    {
        var r = Resolve(Initial(), [GameAction.Attack], [GameAction.Attack]);
        Assert.Equal(2, r.Match.FirstPlayer.Energy); Assert.Equal(2, r.Match.SecondPlayer.Energy);
        Assert.Equal(2, r.Match.RoundNumber); Assert.Equal(1, r.Match.SequenceNumber); Assert.Equal(Now.AddSeconds(1), r.Match.LastActivityAt);
    }
    [Fact] public void ReservedEnergyIsAddedOnce()
    {
        var m = Resolve(Initial(), [GameAction.Reserve], [GameAction.Reserve]).Match;
        Assert.Equal(3, m.FirstPlayer.Energy); Assert.Equal(0, m.FirstPlayer.ReservedEnergy);
        m = Resolve(m, [GameAction.Attack, GameAction.Attack, GameAction.Attack], [GameAction.Reserve, GameAction.Reserve, GameAction.Reserve]).Match;
        Assert.Equal(2, m.FirstPlayer.Energy); Assert.Equal(5, m.SecondPlayer.Energy);
    }
    [Fact] public void FifthBlockAllowedButSixthRejected()
    {
        var m = Initial(); m = m with { FirstPlayer = m.FirstPlayer with { UsedBlockades = 4 } };
        m = Resolve(m, [GameAction.Block], [GameAction.Attack]).Match;
        Assert.Equal(5, m.FirstPlayer.UsedBlockades);
        Assert.Throws<InvalidOperationException>(() => Resolve(m, [GameAction.Block, GameAction.Reserve], [GameAction.Reserve, GameAction.Reserve]));
    }
    [Theory] [InlineData(0)] [InlineData(9999)]
    public void BlockCancelsEntireNormalAttack(int roll)
    { var r = Resolve(Initial(), [GameAction.Attack], [GameAction.Block], roll); Assert.Equal(0, r.FirstPlayerDamage); Assert.Equal(1, r.FirstPlayerAttacksBlocked); Assert.Equal(50, r.Match.SecondPlayer.Health); }
    [Theory] [InlineData(0,15)] [InlineData(999,15)] [InlineData(1000,5)] [InlineData(9999,5)]
    public void CriticalThresholdAndTripleDamage(int roll, int damage)
    { var r = Resolve(Initial(), [GameAction.Attack], [GameAction.Reserve], roll); Assert.Equal(damage, r.FirstPlayerDamage); Assert.Equal(damage == 15 ? 1 : 0, r.FirstPlayerCriticalHits); }
    [Fact] public void ExactlyTenPercentOfRollsAreCritical()
    {
        int criticals = 0;
        for (int roll = 0; roll < 10000; roll++) criticals += Resolve(Initial(), [GameAction.Attack], [GameAction.Reserve], roll).FirstPlayerCriticalHits;
        Assert.Equal(1000, criticals);
    }
    private static MatchState SpecialState(int round = 5)
    { var m = Initial(); return m with { RoundNumber = round, FirstPlayer = m.FirstPlayer with { Energy = 3 } }; }
    [Fact] public void SpecialLockedBeforeFifthRound() => Assert.Throws<InvalidOperationException>(() => Resolve(SpecialState(4), [GameAction.SpecialAttack], [GameAction.Reserve]));
    [Fact] public void SpecialUnlockedAndCannotBeUsedAgain()
    {
        var r = Resolve(SpecialState(), [GameAction.SpecialAttack], [GameAction.Reserve], 0);
        Assert.Equal(20, r.FirstPlayerDamage); Assert.Equal(0, r.FirstPlayerCriticalHits); Assert.True(r.Match.FirstPlayer.HasUsedSpecialAttack);
        var m = r.Match with { FirstPlayer = r.Match.FirstPlayer with { Energy = 3 } };
        Assert.Throws<InvalidOperationException>(() => Resolve(m, [GameAction.SpecialAttack], [GameAction.Reserve, GameAction.Reserve, GameAction.Reserve]));
    }
    [Fact] public void TwoSpecialsInSamePlanRejected()
    { var m = SpecialState(); m = m with { FirstPlayer = m.FirstPlayer with { Energy = 6 } }; Assert.Throws<InvalidOperationException>(() => Resolve(m, [GameAction.SpecialAttack, GameAction.SpecialAttack], [GameAction.Reserve])); }
    [Fact] public void BlockTargetsSpecialBeforeNormalAttack()
    {
        var m = SpecialState(); m = m with { FirstPlayer = m.FirstPlayer with { Energy = 4 } };
        var r = Resolve(m, [GameAction.Attack, GameAction.SpecialAttack], [GameAction.Block]);
        Assert.Equal(5, r.FirstPlayerDamage); Assert.Equal(1, r.FirstPlayerAttacksBlocked); Assert.True(r.Match.FirstPlayer.HasUsedSpecialAttack);
    }
    [Fact] public void KnockoutWins()
    {
        var m = Initial(); m = m with { SecondPlayer = m.SecondPlayer with { Health = 4 } };
        var r = Resolve(m, [GameAction.Attack], [GameAction.Reserve]);
        Assert.Equal(MatchStatus.Finished, r.Match.Status); Assert.Equal(m.FirstPlayer.PlayerId, r.Match.WinnerId); Assert.Equal(0, r.Match.SecondPlayer.Health); Assert.Equal(0, r.Match.FirstPlayer.Energy);
    }
    [Fact] public void SimultaneousKnockoutDraws()
    {
        var m = Initial(); m = m with { FirstPlayer = m.FirstPlayer with { Health = 5 }, SecondPlayer = m.SecondPlayer with { Health = 5 } };
        var r = Resolve(m, [GameAction.Attack], [GameAction.Attack]);
        Assert.Equal(MatchStatus.Finished, r.Match.Status); Assert.Null(r.Match.WinnerId); Assert.Equal(0, r.Match.FirstPlayer.Health); Assert.Equal(0, r.Match.SecondPlayer.Health);
    }
    [Theory] [InlineData(50,40,1)] [InlineData(40,50,2)] [InlineData(50,50,0)]
    public void TenthRoundEndsByHealth(int firstHealth, int secondHealth, int winner)
    {
        var m = Initial(); m = m with { RoundNumber = 10, FirstPlayer = m.FirstPlayer with { Health = firstHealth }, SecondPlayer = m.SecondPlayer with { Health = secondHealth } };
        var r = Resolve(m, [GameAction.Reserve], [GameAction.Reserve]);
        Assert.Equal(MatchStatus.Finished, r.Match.Status); Assert.Equal(10, r.Match.RoundNumber);
        Assert.Equal(winner == 1 ? m.FirstPlayer.PlayerId : winner == 2 ? m.SecondPlayer.PlayerId : (Guid?)null, r.Match.WinnerId);
        Assert.Throws<InvalidOperationException>(() => Resolve(r.Match, [], []));
    }
    [Fact] public void InvalidActionRejected() => Assert.Throws<InvalidOperationException>(() => Resolve(Initial(), [(GameAction)99], [GameAction.Reserve]));
    [Fact] public void WrongPlayerRejected()
    { var m = Initial(); Assert.Throws<InvalidOperationException>(() => new RoundResolver(new FixedRoll(1000)).Resolve(m, new(m.SecondPlayer.PlayerId, [GameAction.Attack]), new(m.SecondPlayer.PlayerId, [GameAction.Reserve]), Now)); }
    [Fact] public void MissingActionsRejected()
    { var m = Initial(); Assert.Throws<InvalidOperationException>(() => new RoundResolver(new FixedRoll(1000)).Resolve(m, new(m.FirstPlayer.PlayerId, null!), new(m.SecondPlayer.PlayerId, [GameAction.Reserve]), Now)); }
}
