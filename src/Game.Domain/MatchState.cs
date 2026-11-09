namespace Game.Domain;

public enum MatchStatus // Estado de la partida
{
    Active,
    Finished
}

public sealed record MatchState(
    Guid MatchId,  // Identificador único de la partida
    int RoundNumber,
    PlayerState FirstPlayer,
    PlayerState SecondPlayer,
    MatchStatus Status,
    Guid? WinnerId,
    long SequenceNumber, // Número de secuencia para el control de versiones
    DateTimeOffset LastActivityAt)
{
    public static MatchState Create(Guid matchId, Guid firstPlayerId, Guid secondPlayerId, DateTimeOffset now) =>
        new(matchId, 1,
            new PlayerState(firstPlayerId, PlayerState.MaximumHealth, 1),
            new PlayerState(secondPlayerId, PlayerState.MaximumHealth, 1),
            MatchStatus.Active, null, 0, now);
}
