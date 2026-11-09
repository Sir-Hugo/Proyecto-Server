namespace Game.Domain;

public sealed record PlayerState(
    Guid PlayerId, // Identificador único del jugador
    int Health,
    int Energy,
    int ReservedEnergy = 0,
    int UsedBlockades = 0,  // Contador de bloqueos utilizados
    bool HasUsedSpecialAttack = false)
{
    public const int MaximumHealth = 50;
}
