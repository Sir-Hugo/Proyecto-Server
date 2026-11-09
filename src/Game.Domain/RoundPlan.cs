namespace Game.Domain;

// Representa el plan de movimientos de un jugador para una ronda.
// IReadOnlyList<GameAction> permite consultar la lista sin modificarla a través de esa referencia.
public sealed record RoundPlan(Guid PlayerId, IReadOnlyList<GameAction> Actions);
