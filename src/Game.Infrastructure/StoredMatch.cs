using Game.Domain;

namespace Game.Infrastructure;

public sealed record StoredParticipant(Guid PlayerId, string AuthSubject, string? Nickname, short Slot);
public sealed record StoredMatch(MatchState State, IReadOnlyList<StoredParticipant> Participants,
    DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt);
