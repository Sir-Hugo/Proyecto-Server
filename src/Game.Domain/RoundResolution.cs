namespace Game.Domain;

// Clase que representa la resolución de una ronda, incluyendo el estado del juego.
public sealed record RoundResolution(
    MatchState Match,
    int FirstPlayerDamage,
    int SecondPlayerDamage,
    int FirstPlayerCriticalHits,
    int SecondPlayerCriticalHits,
    int FirstPlayerAttacksBlocked,
    int SecondPlayerAttacksBlocked
    );
