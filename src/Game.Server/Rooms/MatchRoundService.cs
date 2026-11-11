using Game.Domain;
using Game.Infrastructure;
using Game.Server.Contracts.V1;
using Game.Server.Hubs;
using Microsoft.AspNetCore.SignalR;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Game.Server.Rooms;

public sealed class MatchRoundService(IHubContext<GameHub> hub, ICriticalHitSource criticalSource,
    IMatchSessionStore store, IConfiguration? configuration = null, ILogger<MatchRoundService>? logger = null)
{
    // Transport routing only; confirmed game state is loaded from PostgreSQL.
    private readonly ConcurrentDictionary<(Guid MatchId, string Subject), string> connections = [];
    private readonly ConcurrentDictionary<(Guid MatchId, string Subject), DateTimeOffset> reconnectGrace = [];
    private sealed record Notification(string Subject, string Event, JsonElement Message);
    private sealed record SubmissionResult(JsonElement Acknowledgement, IReadOnlyList<Notification> Outcomes);
    private readonly TimeSpan roundTimeout = TimeSpan.FromMinutes(configuration?.GetValue("MatchLifecycle:RoundTimeoutMinutes", 5) ?? 5);
    private readonly TimeSpan disconnectGrace = TimeSpan.FromSeconds(configuration?.GetValue("MatchLifecycle:DisconnectGraceSeconds", 30) ?? 30);

    public async Task CreateAsync(MatchState match, IReadOnlyDictionary<string, Guid> participants, CancellationToken ct = default)
    {
        match = match with { LastActivityAt = Microseconds(match.LastActivityAt) };
        var session = new MatchSession(match, participants);
        session.RoundDeadlineAt = match.LastActivityAt.Add(roundTimeout);
        session.NewEvents.Add(new(match.MatchId, match.SequenceNumber, "match.started", 1, match.LastActivityAt,
            JsonSerializer.SerializeToElement(new { state = match, reason = "created" })));
        await store.CreateAsync(session, ct);
    }
    public async Task<MatchStatus?> GetMatchStatusAsync(Guid matchId, CancellationToken ct = default) =>
        (await store.ReadAsync(matchId, ct))?.Match.Status;
    public Task<bool> IsPlayerActiveAsync(string subject, CancellationToken ct = default) => store.IsPlayerActiveAsync(subject, ct);
    public Task<Guid?> FindLatestMatchAsync(string subject, CancellationToken ct = default) => store.FindLatestMatchAsync(subject, ct);

    public Task<object> AttachAsync(Guid matchId, string subject, string connectionId, CancellationToken ct = default) => HubGuardAsync<object>(async () =>
    {
        var session = await store.ReadAsync(matchId, ct);
        Authorize(session, subject);
        connections[(matchId, subject)] = connectionId;
        reconnectGrace.TryRemove((matchId,subject),out _);
        return CreateSnapshot(session!, subject);
    });

    public Task SetNicknameAsync(Guid matchId, string subject, string connectionId, string nickname, CancellationToken ct = default) => HubGuardAsync(async () =>
    {
        if (string.IsNullOrEmpty(nickname) || nickname.Length > 10 ||
            !Regex.IsMatch(nickname, @"^[\p{L}\p{N}_-]+$", RegexOptions.CultureInvariant)) throw new HubException("invalid_nickname");
        var messages = await store.MutateAsync(matchId, session =>
        {
            Authorize(session, subject); RequireActive(session);
            if (session.Nicknames.ContainsKey(subject)) throw new HubException("nickname_already_set");
            session.Nicknames[subject] = nickname;
            if (session.Nicknames.Count == 2) session.StartedAt = CaptureTime();
            var notifications = session.Participants.Keys.Select(player => new Notification(player, "match.state.snapshot",
                JsonSerializer.SerializeToElement(CreateSnapshot(session, player)))).ToArray();
            return notifications;
        }, ct);
        connections[(matchId, subject)] = connectionId;
        await PublishAsync(matchId, messages, ct);
    });

    public Task SubmitAsync(string subject, string connectionId, SubmitRoundPlanMessage message, CancellationToken ct = default) => HubGuardAsync(async () =>
    {
        var validation = SubmitRoundPlanMessageValidator.Validate(message);
        if (!validation.IsValid) throw new HubException($"invalid_message:{validation.Issues[0].Code}");
        var request = validation.Request!;
        var response = await store.MutateAsync(request.MatchId, session =>
        {
            Authorize(session, subject);
            var key = new CommandKey(subject, request.RequestId);
            byte[] fingerprint = SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
            { request.MatchId, request.ExpectedSequence, Actions = request.Actions.Select(action => action.ToString()).ToArray() }));
            if (session.Submissions.TryGetValue(key, out var previous))
            {
                if (!CryptographicOperations.FixedTimeEquals(previous.Fingerprint, fingerprint)) throw new HubException("idempotency_key_reused");
                return new SubmissionResult(previous.Acknowledgement, previous.Outcome is null ? [] :
                    [new(subject, previous.Outcome.EventName, previous.Outcome.Message)]);
            }
            RequireActive(session);
            if (session.Nicknames.Count != 2) throw new HubException("nicknames_required");
            if (request.ExpectedSequence != session.Match.SequenceNumber) throw new HubException("stale_sequence");
            if (session.PendingPlans.ContainsKey(subject)) throw new HubException("round_plan_already_submitted");
            var plan = new RoundPlan(session.Participants[subject], request.Actions);
            try
            {
                var player = session.Match.FirstPlayer.PlayerId == plan.PlayerId ? session.Match.FirstPlayer : session.Match.SecondPlayer;
                RoundResolver.ValidatePlan(plan, player, session.Match.RoundNumber);
            }
            catch (InvalidOperationException) { throw new HubException("invalid_round_plan:rules_violation"); }
            var ack = JsonSerializer.SerializeToElement(new { type = "round.plan.accepted", version = 1, matchId = request.MatchId,
                requestId = request.RequestId, sequence = session.Match.SequenceNumber });
            session.Submissions.Add(key, new(fingerprint, ack));
            session.PendingPlans.Add(subject, new(plan, key));
            if (session.PendingPlans.Count != 2) return new SubmissionResult(ack, []);

            var before = session.Match;
            string first = session.Participants.Single(pair => pair.Value == before.FirstPlayer.PlayerId).Key;
            string second = session.Participants.Single(pair => pair.Value == before.SecondPlayer.PlayerId).Key;
            var firstPending = session.PendingPlans[first]; var secondPending = session.PendingPlans[second];
            var rolls = new RecordingCriticalSource(criticalSource);
            var resolution = new RoundResolver(rolls).Resolve(before, firstPending.Plan, secondPending.Plan, CaptureTime());
            session.Match = resolution.Match;
            session.RoundDeadlineAt = session.Match.Status == MatchStatus.Active ? session.Match.LastActivityAt.Add(roundTimeout) : null;
            if (session.Match.Status == MatchStatus.Finished)
            {
                session.CompletedAt = session.Match.LastActivityAt;
                session.TerminalReason = "completed";
            }
            session.NewEvents.Add(new(request.MatchId, session.Match.SequenceNumber, "round.resolved", 1, session.Match.LastActivityAt,
                JsonSerializer.SerializeToElement(new { before, after = session.Match, firstPlan = firstPending.Plan, secondPlan = secondPending.Plan,
                    criticalRolls = rolls.Values, resolution, reason = session.Match.Status == MatchStatus.Active ? "round" : "completed" })));
            List<Notification> outcomes = [];
            foreach (var pending in new[] { firstPending, secondPending })
            {
                var result = JsonSerializer.SerializeToElement(CreateResolution(resolution, before.RoundNumber, pending.CommandKey.Subject, session));
                session.Submissions[pending.CommandKey].Outcome = new("round.resolved", result);
                outcomes.Add(new(pending.CommandKey.Subject, "round.resolved", result));
            }
            session.PendingPlans.Clear();
            Enqueue(session, outcomes, session.Match.SequenceNumber);
            return new SubmissionResult(ack, outcomes);
        }, ct);
        connections[(request.MatchId, subject)] = connectionId;
        await PublishAsync(request.MatchId, [new(subject, "round.plan.accepted", response.Acknowledgement)], ct);
        await PublishAsync(request.MatchId, response.Outcomes, ct);
    });

    public Task AbandonAsync(Guid matchId, string subject, CancellationToken ct = default) => HubGuardAsync(async () =>
    {
        var notifications = await store.MutateAsync(matchId, session =>
        {
            Authorize(session, subject); RequireActive(session);
            return Finish(session, session.Participants.Single(pair => pair.Key != subject).Value, "abandonment", CaptureTime());
        }, ct);
        await PublishAsync(matchId, notifications, ct);
    });

    public async Task<int> ExpireInactiveMatchesAsync(DateTimeOffset now, TimeSpan inactivityLimit, CancellationToken ct)
    {
        now = Microseconds(now);
        int count = 0;
        foreach (Guid id in await store.FindExpiredAsync(now, ct))
        {
            var notifications = await store.MutateAsync(id, session =>
            {
                if (session.Match.Status != MatchStatus.Active || now - session.Match.LastActivityAt < inactivityLimit) return Array.Empty<Notification>();
                Guid? winner = session.PendingPlans.Count == 1 ? session.PendingPlans.Values.Single().Plan.PlayerId : null;
                return Finish(session, winner, "inactivity", now);
            }, ct);
            count += notifications.Count(item => connections.ContainsKey((id, item.Subject)));
            await PublishAsync(id, notifications, ct);
        }
        return count;
    }

    private static Notification[] Finish(MatchSession session, Guid? winner, string reason, DateTimeOffset now)
    {
        var before = session.Match;
        session.Match = before with { Status = MatchStatus.Finished, WinnerId = winner, SequenceNumber = before.SequenceNumber + 1, LastActivityAt = now };
        session.CompletedAt = now;
        session.RoundDeadlineAt = null;
        session.TerminalReason = reason;
        var notifications = session.Participants.Keys.Select(subject => new Notification(subject, "match.ended",
            JsonSerializer.SerializeToElement(CreateEndedMessage(session, subject, reason)))).ToArray();
        foreach (var pair in session.PendingPlans)
            session.Submissions[pair.Value.CommandKey].Outcome = new("match.ended", notifications.Single(item => item.Subject == pair.Key).Message);
        session.PendingPlans.Clear();
        session.NewEvents.Add(new(session.Match.MatchId, session.Match.SequenceNumber, "match.ended", 1, now,
            JsonSerializer.SerializeToElement(new { before, after = session.Match, reason })));
        Enqueue(session, notifications, session.Match.SequenceNumber);
        return notifications;
    }

    private static void Enqueue(MatchSession session, IEnumerable<Notification> notifications, long sequence)
    {
        DateTimeOffset now=CaptureTime();
        foreach(var item in notifications) session.NewNotifications.Add(new(Guid.NewGuid(),session.Match.MatchId,item.Subject,item.Event,sequence,item.Message,now));
    }

    private async Task PublishAsync(Guid matchId, IEnumerable<Notification> notifications, CancellationToken ct)
    {
        foreach (var notification in notifications)
        {
            if (!connections.TryGetValue((matchId, notification.Subject), out string? connection)) continue;
            try { await hub.Clients.Client(connection).SendAsync(notification.Event, notification.Message, ct); }
            catch (Exception)
            {
                logger?.LogWarning("SignalR delivery failed after commit for match {MatchId}, event {Event}", matchId, notification.Event);
            }
        }
    }
    public async Task<int> DeliverPendingAsync(Guid? matchId=null,string? subject=null,CancellationToken ct=default)
    {
        int delivered=0;
        foreach(var item in await store.ReadPendingNotificationsAsync(matchId,subject,100,ct))
        {
            if(!connections.TryGetValue((item.MatchId,item.Subject),out string? connection)) continue;
            try
            {
                await hub.Clients.Client(connection).SendAsync(item.EventName,item.Message,ct);
                await store.MarkNotificationDeliveredAsync(item.Id,ct); delivered++;
            }
            catch(Exception)
            {
                int seconds=Math.Min(60,1 << Math.Min(item.AttemptCount,6));
                await store.MarkNotificationFailedAsync(item.Id,"transport_failure",CaptureTime().AddSeconds(seconds),ct);
                logger?.LogWarning("Outbox delivery failed for match {MatchId}, event {Event}",item.MatchId,item.EventName);
            }
        }
        return delivered;
    }
    public void DetachConnection(string connectionId)
    {
        foreach(var item in connections.Where(pair=>pair.Value==connectionId).ToArray())
        {
            connections.TryRemove(item.Key,out _);
            reconnectGrace[item.Key]=CaptureTime().Add(disconnectGrace);
        }
    }
    public bool IsWithinReconnectGrace(Guid matchId,string subject,DateTimeOffset now) =>
        reconnectGrace.TryGetValue((matchId,subject),out var until) && now <= until;
    private static void Authorize(MatchSession? session, string subject)
    {
        if (session is null || !session.Participants.ContainsKey(subject)) throw new HubException("not_match_participant");
    }
    private static void RequireActive(MatchSession session)
    {
        if (session.Match.Status != MatchStatus.Active) throw new HubException("match_completed");
    }
    private static async Task HubGuardAsync(Func<Task> operation)
    { await HubGuardAsync(async () => { await operation(); return true; }); }
    private static async Task<T> HubGuardAsync<T>(Func<Task<T>> operation)
    {
        try { return await operation(); }
        catch (PersistenceException) { throw new HubException("database_unavailable"); }
        catch (MatchOperationException exception) { throw new HubException(exception.Code); }
    }
    private static DateTimeOffset Microseconds(DateTimeOffset value) => new(value.UtcTicks - value.UtcTicks % 10, TimeSpan.Zero);
    private static DateTimeOffset CaptureTime() => Microseconds(DateTimeOffset.UtcNow);
    private sealed class RecordingCriticalSource(ICriticalHitSource source) : ICriticalHitSource
    {
        public List<int> Values { get; } = [];
        public int NextBasisPointRoll() { int value = source.NextBasisPointRoll(); Values.Add(value); return value; }
    }
    private static object CreateSnapshot(MatchSession session, string subject)
    {
        PlayerState you = session.Match.FirstPlayer.PlayerId == session.Participants[subject]
            ? session.Match.FirstPlayer : session.Match.SecondPlayer;
        PlayerState opponent = session.Match.FirstPlayer.PlayerId == you.PlayerId
            ? session.Match.SecondPlayer : session.Match.FirstPlayer;
        return new
        {
            type = "match.state.snapshot", version = 1, matchId = session.Match.MatchId,
            sequence = session.Match.SequenceNumber, roundNumber = session.Match.RoundNumber,
            winnerId = session.Match.WinnerId,
            hasSubmittedPlan = session.PendingPlans.ContainsKey(subject),
            roundDeadlineAt = session.RoundDeadlineAt,
            terminalReason = session.TerminalReason,
            status = session.Match.Status != MatchStatus.Active ? "completed" : session.Nicknames.Count == 2 ? "active" : "waitingForNicknames",
            you = new { playerId = you.PlayerId, isHost = you.PlayerId == session.Match.FirstPlayer.PlayerId, nickname = session.Nicknames.GetValueOrDefault(subject), health = you.Health, energy = you.Energy,
                usedBlockades = you.UsedBlockades, hasUsedSpecialAttack = you.HasUsedSpecialAttack },
            opponent = new { playerId = opponent.PlayerId, isHost = opponent.PlayerId == session.Match.FirstPlayer.PlayerId, nickname = session.Nicknames.GetValueOrDefault(
                session.Participants.Single(pair => pair.Value == opponent.PlayerId).Key), health = opponent.Health }
        };
    }

    private static object CreateEndedMessage(MatchSession session, string subject, string reason)
    {
        PlayerState you = session.Match.FirstPlayer.PlayerId == session.Participants[subject]
            ? session.Match.FirstPlayer : session.Match.SecondPlayer;
        PlayerState opponent = session.Match.FirstPlayer.PlayerId == you.PlayerId
            ? session.Match.SecondPlayer : session.Match.FirstPlayer;
        return new
        {
            type = "match.ended", version = 1, matchId = session.Match.MatchId,
            sequence = session.Match.SequenceNumber, roundNumber = session.Match.RoundNumber,
            reason, status = "completed", winnerId = session.Match.WinnerId,
            hasSubmittedPlan = false, roundDeadlineAt = (DateTimeOffset?)null, terminalReason = reason,
            you = new { playerId = you.PlayerId, isHost = you.PlayerId == session.Match.FirstPlayer.PlayerId, nickname = session.Nicknames.GetValueOrDefault(subject), health = you.Health, energy = you.Energy,
                usedBlockades = you.UsedBlockades, hasUsedSpecialAttack = you.HasUsedSpecialAttack },
            opponent = new { playerId = opponent.PlayerId, isHost = opponent.PlayerId == session.Match.FirstPlayer.PlayerId, nickname = session.Nicknames.GetValueOrDefault(
                session.Participants.Single(pair => pair.Value == opponent.PlayerId).Key), health = opponent.Health }
        };
    }

    private static object CreateResolution(RoundResolution result, int resolvedRound, string subject, MatchSession session)
    {
        bool isFirst = session.Participants[subject] == result.Match.FirstPlayer.PlayerId;
        PlayerState you = isFirst ? result.Match.FirstPlayer : result.Match.SecondPlayer;
        PlayerState opponent = isFirst ? result.Match.SecondPlayer : result.Match.FirstPlayer;
        return new
        {
            type = "round.resolved", version = 1, matchId = result.Match.MatchId,
            sequence = result.Match.SequenceNumber, roundNumber = resolvedRound,
            yourDamage = isFirst ? result.FirstPlayerDamage : result.SecondPlayerDamage,
            opponentDamage = isFirst ? result.SecondPlayerDamage : result.FirstPlayerDamage,
            yourCriticalHits = isFirst ? result.FirstPlayerCriticalHits : result.SecondPlayerCriticalHits,
            opponentCriticalHits = isFirst ? result.SecondPlayerCriticalHits : result.FirstPlayerCriticalHits,
            yourAttacksBlocked = isFirst ? result.FirstPlayerAttacksBlocked : result.SecondPlayerAttacksBlocked,
            opponentAttacksBlocked = isFirst ? result.SecondPlayerAttacksBlocked : result.FirstPlayerAttacksBlocked,
            status = result.Match.Status == MatchStatus.Active ? "active" : "completed",
            winnerId = result.Match.WinnerId,
            hasSubmittedPlan = false, roundDeadlineAt = session.RoundDeadlineAt, terminalReason = session.TerminalReason,
            you = new { playerId = you.PlayerId, isHost = you.PlayerId == result.Match.FirstPlayer.PlayerId, nickname = session.Nicknames.GetValueOrDefault(subject), health = you.Health, energy = you.Energy,
                usedBlockades = you.UsedBlockades, hasUsedSpecialAttack = you.HasUsedSpecialAttack },
            opponent = new { playerId = opponent.PlayerId, isHost = opponent.PlayerId == result.Match.FirstPlayer.PlayerId, nickname = session.Nicknames.GetValueOrDefault(
                session.Participants.Single(pair => pair.Value == opponent.PlayerId).Key), health = opponent.Health }
        };
    }

}
