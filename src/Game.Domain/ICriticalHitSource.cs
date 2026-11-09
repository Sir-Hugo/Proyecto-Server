namespace Game.Domain;

/// <summary>Supplies an unbiased value in [0, 9999] for critical-hit checks.</summary>
public interface ICriticalHitSource
{
    int NextBasisPointRoll();
}
