using System.Collections.Concurrent;
using Game.Infrastructure;

namespace Game.Server.Tests;

// Unit-test double only. Production DI has no in-memory fallback.
internal sealed class InMemoryMatchSessionStore : IMatchSessionStore
{
    private readonly ConcurrentDictionary<Guid, MatchSession> sessions = [];
    private readonly object gate = new();
    public void Seed(MatchSession session) { lock (gate) sessions[session.Match.MatchId] = Copy(session); }
    public Task CreateAsync(MatchSession session, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); Seed(session); return Task.CompletedTask; }
    public Task<MatchSession?> ReadAsync(Guid id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate) return Task.FromResult(sessions.TryGetValue(id, out var session) ? Copy(session) : null);
    }
    public Task<T> MutateAsync<T>(Guid id, Func<MatchSession, T> operation, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (!sessions.TryGetValue(id, out var current)) throw new MatchOperationException("not_match_participant");
            var next = Copy(current);
            T result = operation(next);
            sessions[id] = next;
            return Task.FromResult(result);
        }
    }
    public Task<IReadOnlyList<Guid>> FindExpiredAsync(DateTimeOffset deadline, CancellationToken cancellationToken = default)
    {
        lock (gate) return Task.FromResult<IReadOnlyList<Guid>>(sessions.Values.Where(session => session.Match.Status == Domain.MatchStatus.Active &&
            (session.RoundDeadlineAt ?? session.Match.LastActivityAt) <= deadline).Select(session => session.Match.MatchId).ToArray());
    }
    public Task<bool> IsPlayerActiveAsync(string subject, CancellationToken cancellationToken = default)
    {
        lock (gate) return Task.FromResult(sessions.Values.Any(session => session.Match.Status == Domain.MatchStatus.Active && session.Participants.ContainsKey(subject)));
    }
    public Task<Guid?> FindLatestMatchAsync(string subject, CancellationToken cancellationToken = default)
    {
        lock (gate) return Task.FromResult<Guid?>(sessions.Values.Where(s => s.Participants.ContainsKey(subject))
            .OrderByDescending(s => s.Match.Status == Domain.MatchStatus.Active).ThenByDescending(s => s.CreatedAt)
            .Select(s => (Guid?)s.Match.MatchId).FirstOrDefault());
    }
    public Task<IReadOnlyList<PendingNotification>> ReadPendingNotificationsAsync(Guid? matchId = null, string? subject = null,
        int limit = 100, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<PendingNotification>>([]);
    public Task MarkNotificationDeliveredAsync(Guid id, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task MarkNotificationFailedAsync(Guid id, string errorCode, DateTimeOffset nextAttemptAt, CancellationToken cancellationToken = default) => Task.CompletedTask;
    private static MatchSession Copy(MatchSession session)
    {
        var copy = new MatchSession(session.Match, session.Participants)
        { CreatedAt = session.CreatedAt, StartedAt = session.StartedAt, CompletedAt = session.CompletedAt,
          RoundDeadlineAt = session.RoundDeadlineAt, TerminalReason = session.TerminalReason };
        foreach (var pair in session.Nicknames) copy.Nicknames.Add(pair.Key, pair.Value);
        foreach (var pair in session.PendingPlans) copy.PendingPlans.Add(pair.Key, pair.Value);
        foreach (var pair in session.Submissions) copy.Submissions.Add(pair.Key,
            new(pair.Value.Fingerprint, pair.Value.Acknowledgement) { Outcome = pair.Value.Outcome });
        return copy;
    }
}
