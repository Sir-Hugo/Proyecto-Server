using Game.Domain;
using Game.Server.Contracts.V1;
using Game.Server.Hubs;
using Microsoft.AspNetCore.SignalR;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace Game.Server.Rooms;

/// <summary>Coordina el estado en memoria y los planes simultáneos de cada partida.</summary>
public sealed class MatchRoundService(IHubContext<GameHub> hub, RoundResolver resolver)
{
    private readonly ConcurrentDictionary<Guid, MatchSession> matches = [];

    public async Task<int> ExpireInactiveMatchesAsync(DateTimeOffset now, TimeSpan inactivityLimit, CancellationToken cancellationToken)
    {
        List<(string ConnectionId, object Message)> notifications = [];
        foreach (MatchSession session in matches.Values)
        {
            lock (session.Gate)
            {
                if (session.Match.Status != MatchStatus.Active || now - session.Match.LastActivityAt < inactivityLimit)
                    continue;

                Dictionary<string, PendingRoundPlan> pendingPlans = new(session.PendingPlans);
                string[] submittedSubjects = pendingPlans.Keys.ToArray();
                Guid? winnerId = submittedSubjects.Length == 1
                    ? session.Participants[submittedSubjects[0]]
                    : null;
                session.Match = session.Match with
                {
                    Status = MatchStatus.Finished,
                    WinnerId = winnerId,
                    SequenceNumber = session.Match.SequenceNumber + 1,
                    LastActivityAt = now
                };
                foreach ((string subject, string connectionId) in session.Connections)
                {
                    object message = CreateEndedMessage(session, subject, "inactivity");
                    notifications.Add((connectionId, message));
                    if (pendingPlans.TryGetValue(subject, out PendingRoundPlan? pending) &&
                        session.Submissions.TryGetValue(pending.CommandKey, out SubmissionRecord? record))
                        record.Outcome = new CachedOutcome("match.ended", message);
                }
                session.PendingPlans.Clear();
            }
        }

        foreach ((string connectionId, object message) in notifications)
        {
            await hub.Clients.Client(connectionId).SendAsync("match.ended", message, cancellationToken);
        }

        return notifications.Count;
    }

    public async Task AbandonAsync(Guid matchId, string subject)
    {
        MatchSession session = GetParticipant(matchId, subject);
        List<(string ConnectionId, object Message)> notifications = [];
        lock (session.Gate)
        {
            if (session.Match.Status != MatchStatus.Active)
                throw new HubException("match_completed");

            string opponentSubject = session.Participants.Keys.Single(candidate => candidate != subject);
            session.Match = session.Match with
            {
                Status = MatchStatus.Finished,
                WinnerId = session.Participants[opponentSubject],
                SequenceNumber = session.Match.SequenceNumber + 1,
                LastActivityAt = DateTimeOffset.UtcNow
            };

            Dictionary<string, PendingRoundPlan> pendingPlans = new(session.PendingPlans);
            foreach ((string playerSubject, string connectionId) in session.Connections)
            {
                object message = CreateEndedMessage(session, playerSubject, "abandonment");
                notifications.Add((connectionId, message));
                if (pendingPlans.TryGetValue(playerSubject, out PendingRoundPlan? pending) &&
                    session.Submissions.TryGetValue(pending.CommandKey, out SubmissionRecord? record))
                    record.Outcome = new CachedOutcome("match.ended", message);
            }
            session.PendingPlans.Clear();
        }

        foreach ((string connectionId, object message) in notifications)
            await hub.Clients.Client(connectionId).SendAsync("match.ended", message);
    }

    public void Register(MatchState match, IReadOnlyDictionary<string, Guid> participants)
    {
        matches.TryAdd(match.MatchId, new MatchSession(match, participants));
    }

    public MatchStatus? GetMatchStatus(Guid matchId)
    {
        if (!matches.TryGetValue(matchId, out MatchSession? session)) return null;
        lock (session.Gate) return session.Match.Status;
    }

    public object Attach(Guid matchId, string subject, string connectionId)
    {
        MatchSession session = GetParticipant(matchId, subject);
        lock (session.Gate)
        {
            session.Connections[subject] = connectionId;
            return CreateSnapshot(session, subject);
        }
    }

    public async Task SetNicknameAsync(Guid matchId, string subject, string connectionId, string nickname)
    {
        if (string.IsNullOrEmpty(nickname) || nickname.Length > 10 ||
            !Regex.IsMatch(nickname, @"^[\p{L}\p{N}_-]+$", RegexOptions.CultureInvariant))
            throw new HubException("invalid_nickname");

        List<(string ConnectionId, object Snapshot)> notifications = [];
        MatchSession session = GetParticipant(matchId, subject);
        lock (session.Gate)
        {
            if (session.Match.Status != MatchStatus.Active)
                throw new HubException("match_completed");
            if (session.Nicknames.ContainsKey(subject))
                throw new HubException("nickname_already_set");

            session.Nicknames[subject] = nickname;
            session.Connections[subject] = connectionId;
            foreach ((string playerSubject, string playerConnection) in session.Connections)
                notifications.Add((playerConnection, CreateSnapshot(session, playerSubject)));
        }

        foreach ((string playerConnection, object snapshot) in notifications)
            await hub.Clients.Client(playerConnection).SendAsync("match.state.snapshot", snapshot);
    }

    public async Task SubmitAsync(string subject, string connectionId, SubmitRoundPlanMessage message)
    {
        RoundPlanMessageValidationResult validation = SubmitRoundPlanMessageValidator.Validate(message);
        if (!validation.IsValid)
        {
            throw new HubException($"invalid_message:{validation.Issues[0].Code}");
        }

        MatchSession session = GetParticipant(validation.Request!.MatchId, subject);
        object acknowledgement;
        CachedOutcome? outcome = null;
        string? firstConnection = null;
        string? secondConnection = null;
        CachedOutcome? outcomeForFirst = null;
        CachedOutcome? outcomeForSecond = null;
        lock (session.Gate)
        {
            ValidatedRoundPlanRequest request = validation.Request;
            Guid playerId = session.Participants[subject];
            CommandKey commandKey = new(subject, request.RequestId);
            string fingerprint = CreateFingerprint(request);
            if (session.Submissions.TryGetValue(commandKey, out SubmissionRecord? previousSubmission))
            {
                if (!string.Equals(previousSubmission.Fingerprint, fingerprint, StringComparison.Ordinal))
                    throw new HubException("idempotency_key_reused");
                acknowledgement = previousSubmission.Acknowledgement;
                outcome = previousSubmission.Outcome;
            }
            else
            {
                if (session.Match.Status != MatchStatus.Active)
                    throw new HubException("match_completed");
                if (session.Nicknames.Count != 2)
                    throw new HubException("nicknames_required");
                if (request.ExpectedSequence != session.Match.SequenceNumber)
                    throw new HubException("stale_sequence");
                if (session.PendingPlans.ContainsKey(subject))
                    throw new HubException("round_plan_already_submitted");

                RoundPlan plan = new(playerId, request.Actions);
                try
                {
                    // Valida también todas las reglas propias del juego antes de guardar el plan.
                    ValidateIndividualPlan(session.Match, plan);
                }
                catch (InvalidOperationException exception)
                {
                    throw new HubException($"invalid_round_plan:{exception.Message}");
                }

                acknowledgement = new
                {
                    type = "round.plan.accepted", version = 1, matchId = request.MatchId,
                    requestId = request.RequestId, sequence = session.Match.SequenceNumber
                };
                session.Submissions.Add(commandKey, new SubmissionRecord(fingerprint, acknowledgement));
                session.PendingPlans[subject] = new PendingRoundPlan(plan, commandKey);
                session.Connections[subject] = connectionId;

                if (session.PendingPlans.Count == 2)
                {
                    string firstSubject = session.Participants.Single(pair => pair.Value == session.Match.FirstPlayer.PlayerId).Key;
                    string secondSubject = session.Participants.Single(pair => pair.Value == session.Match.SecondPlayer.PlayerId).Key;
                    RoundResolution result = resolver.Resolve(
                        session.Match,
                        session.PendingPlans[firstSubject].Plan,
                        session.PendingPlans[secondSubject].Plan,
                        DateTimeOffset.UtcNow);
                    MatchState previous = session.Match;
                    session.Match = result.Match;
                    PendingRoundPlan firstPending = session.PendingPlans[firstSubject];
                    PendingRoundPlan secondPending = session.PendingPlans[secondSubject];
                    session.PendingPlans.Clear();
                    firstConnection = session.Connections.GetValueOrDefault(firstSubject);
                    secondConnection = session.Connections.GetValueOrDefault(secondSubject);
                    outcomeForFirst = new CachedOutcome("round.resolved", CreateResolution(result, previous.RoundNumber, firstSubject, session));
                    outcomeForSecond = new CachedOutcome("round.resolved", CreateResolution(result, previous.RoundNumber, secondSubject, session));
                    session.Submissions[firstPending.CommandKey].Outcome = outcomeForFirst;
                    session.Submissions[secondPending.CommandKey].Outcome = outcomeForSecond;
                    if (firstPending.CommandKey.Subject == subject) outcome = outcomeForFirst;
                    if (secondPending.CommandKey.Subject == subject) outcome = outcomeForSecond;
                }
            }
        }

        await hub.Clients.Client(connectionId).SendAsync("round.plan.accepted", acknowledgement);

        if (outcomeForFirst is not null && firstConnection is not null)
            await hub.Clients.Client(firstConnection).SendAsync(outcomeForFirst.EventName, outcomeForFirst.Message);
        if (outcomeForSecond is not null && secondConnection is not null)
            await hub.Clients.Client(secondConnection).SendAsync(outcomeForSecond.EventName, outcomeForSecond.Message);
        else if (outcome is not null && outcomeForFirst is null && outcomeForSecond is null)
            await hub.Clients.Client(connectionId).SendAsync(outcome.EventName, outcome.Message);
    }

    private static void ValidateIndividualPlan(MatchState match, RoundPlan plan)
    {
        PlayerState player = match.FirstPlayer.PlayerId == plan.PlayerId ? match.FirstPlayer : match.SecondPlayer;
        // Resolve con un plan vacío no es apropiado; duplicamos aquí las comprobaciones
        // individuales para no consumir críticos hasta que llegue el segundo plan.
        if (plan.Actions.Any(action => !Enum.IsDefined(action)) ||
            plan.Actions.Sum(MatchRules.EnergyCostFor) != player.Energy)
            throw new InvalidOperationException("all_energy_must_be_spent");
        int blocks = plan.Actions.Count(action => action == GameAction.Block);
        if (player.UsedBlockades + blocks > MatchRules.MaximumBlockadesPerMatch)
            throw new InvalidOperationException("blockade_limit_exceeded");
        int specials = plan.Actions.Count(action => action == GameAction.SpecialAttack);
        if (specials > 0 && match.RoundNumber < MatchRules.SpecialAttackUnlockRound)
            throw new InvalidOperationException("special_attack_locked");
        if (specials > 1 || (specials > 0 && player.HasUsedSpecialAttack))
            throw new InvalidOperationException("special_attack_already_used");
    }

    private MatchSession GetParticipant(Guid matchId, string subject)
    {
        if (!matches.TryGetValue(matchId, out MatchSession? session) || !session.Participants.ContainsKey(subject))
            throw new HubException("not_match_participant");
        return session;
    }

    private static string CreateFingerprint(ValidatedRoundPlanRequest request) =>
        $"{request.MatchId:N}|{request.ExpectedSequence}|{string.Join(',', request.Actions)}";

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
            you = new { playerId = you.PlayerId, isHost = you.PlayerId == result.Match.FirstPlayer.PlayerId, nickname = session.Nicknames.GetValueOrDefault(subject), health = you.Health, energy = you.Energy,
                usedBlockades = you.UsedBlockades, hasUsedSpecialAttack = you.HasUsedSpecialAttack },
            opponent = new { playerId = opponent.PlayerId, isHost = opponent.PlayerId == result.Match.FirstPlayer.PlayerId, nickname = session.Nicknames.GetValueOrDefault(
                session.Participants.Single(pair => pair.Value == opponent.PlayerId).Key), health = opponent.Health }
        };
    }

    private readonly record struct CommandKey(string Subject, Guid RequestId);

    private sealed record PendingRoundPlan(RoundPlan Plan, CommandKey CommandKey);

    private sealed record CachedOutcome(string EventName, object Message);

    private sealed class SubmissionRecord(string fingerprint, object acknowledgement)
    {
        public string Fingerprint { get; } = fingerprint;
        public object Acknowledgement { get; } = acknowledgement;
        public CachedOutcome? Outcome { get; set; }
    }

    private sealed class MatchSession(MatchState match, IReadOnlyDictionary<string, Guid> participants)
    {
        public object Gate { get; } = new();
        public MatchState Match { get; set; } = match;
        public Dictionary<string, Guid> Participants { get; } = new(participants);
        public Dictionary<string, string> Connections { get; } = [];
        public Dictionary<string, string> Nicknames { get; } = [];
        public Dictionary<string, PendingRoundPlan> PendingPlans { get; } = [];
        public Dictionary<CommandKey, SubmissionRecord> Submissions { get; } = [];
    }
}
