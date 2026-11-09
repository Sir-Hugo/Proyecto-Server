using System.Security.Cryptography;

namespace Game.Domain;

/// <summary>Cryptographically secure critical-hit source for production use.</summary>
public sealed class SecureCriticalHitSource : ICriticalHitSource
{
    public int NextBasisPointRoll() => RandomNumberGenerator.GetInt32(0, 10_000);
}
