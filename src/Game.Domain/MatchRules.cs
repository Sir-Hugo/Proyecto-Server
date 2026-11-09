namespace Game.Domain;

// Clase que contiene las reglas del juego, como costos de energía y daño por ataque.
public static class MatchRules
{
    public const int EnergyPerRound = 2;
    public const int EnergyCostPerAction = 1;
    public const int AttackDamage = 5;
    public const int CriticalDamageMultiplier = 3;
    public const int CriticalChanceBasisPoints = 1_000;
    public const int MaximumBlockadesPerMatch = 5;
    public const int SpecialAttackEnergyCost = 3;
    public const int SpecialAttackDamage = 20;
    public const int SpecialAttackUnlockRound = 5;
    public const int MaximumRounds = 10;
    public static readonly TimeSpan InactivityLimit = TimeSpan.FromMinutes(5);

    public static int EnergyCostFor(GameAction action) => action switch
    {
        GameAction.Attack or GameAction.Block or GameAction.Reserve => EnergyCostPerAction,
        GameAction.SpecialAttack => SpecialAttackEnergyCost,
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Unknown game action.")
    };
}
