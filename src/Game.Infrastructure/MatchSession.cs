using Game.Domain;
using System.Text.Json;

namespace Game.Infrastructure;

public readonly record struct CommandKey(string Subject, Guid RequestId);
public sealed record PendingRoundPlan(RoundPlan Plan, CommandKey CommandKey);
public sealed record CachedOutcome(string EventName, JsonElement Message);
public sealed record PendingNotification(Guid Id, Guid MatchId, string Subject, string EventName,
    long Sequence, JsonElement Message, DateTimeOffset CreatedAt, int AttemptCount = 0);
public sealed class SubmissionRecord(byte[] fingerprint, JsonElement acknowledgement)
{
    public byte[] Fingerprint { get; } = fingerprint;
    public JsonElement Acknowledgement { get; } = acknowledgement;
    public CachedOutcome? Outcome { get; set; }
}

// Loaded afresh inside a transaction; never retained as a production state cache.
public sealed class MatchSession(MatchState match, IReadOnlyDictionary<string, Guid> participants)
{
    public MatchState Match { get; set; } = match;
    public DateTimeOffset CreatedAt { get; set; } = match.LastActivityAt;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? RoundDeadlineAt { get; set; }
    public string? TerminalReason { get; set; }
    public Dictionary<string, Guid> Participants { get; } = new(participants);
    public Dictionary<string, string> Nicknames { get; } = [];
    public Dictionary<string, PendingRoundPlan> PendingPlans { get; } = [];
    public Dictionary<CommandKey, SubmissionRecord> Submissions { get; } = [];
    public List<StoredEvent> NewEvents { get; } = [];
    public List<PendingNotification> NewNotifications { get; } = [];
}

public sealed class MatchOperationException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

public interface IMatchSessionStore
{
    Task CreateAsync(MatchSession session, CancellationToken cancellationToken = default);
    Task<MatchSession?> ReadAsync(Guid matchId, CancellationToken cancellationToken = default);
    Task<T> MutateAsync<T>(Guid matchId, Func<MatchSession, T> operation, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Guid>> FindExpiredAsync(DateTimeOffset deadline, CancellationToken cancellationToken = default);
    Task<bool> IsPlayerActiveAsync(string subject, CancellationToken cancellationToken = default);
    Task<Guid?> FindLatestMatchAsync(string subject, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PendingNotification>> ReadPendingNotificationsAsync(Guid? matchId = null, string? subject = null,
        int limit = 100, CancellationToken cancellationToken = default);
    Task MarkNotificationDeliveredAsync(Guid id, CancellationToken cancellationToken = default);
    Task MarkNotificationFailedAsync(Guid id, string errorCode, DateTimeOffset nextAttemptAt,
        CancellationToken cancellationToken = default);
}
