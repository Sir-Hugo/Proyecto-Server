using Game.Domain;
using Game.Server.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace Game.Server.Rooms;

public sealed class MatchmakingService(IHubContext<GameHub> hubContext, IConfiguration configuration, MatchRoundService roundService)
{
    private readonly object gate = new();
    private readonly List<QueueEntry> entries = [];
    private readonly Dictionary<Guid, MatchState> matches = [];
    private readonly Dictionary<Guid, Dictionary<string, Guid>> participants = [];
    private readonly TimeSpan maximumWait = TimeSpan.FromMinutes(
        configuration.GetValue("Matchmaking:MaximumWaitMinutes", 4));

    public async Task JoinAsync(string subject, string connectionId, Guid requestId)
    {
        QueueEntry? waitingPlayer;
        QueueEntry newPlayer;
        Guid? matchedMatchId = null;
        lock (gate)
        {
            QueueEntry? existing = entries.FirstOrDefault(entry => entry.Subject == subject);
            if (existing is not null)
            {
                existing.ConnectionId = connectionId;
                existing.RequestId = requestId;
                newPlayer = existing;
                waitingPlayer = null;
            }
            else
            {
                newPlayer = new QueueEntry(subject, connectionId, requestId, DateTimeOffset.UtcNow);
                waitingPlayer = entries.FirstOrDefault(entry => entry.Subject != subject);
                if (waitingPlayer is null) entries.Add(newPlayer);
                else
                {
                    entries.Remove(waitingPlayer);
                    Guid playerOneId = Guid.NewGuid();
                    Guid playerTwoId = Guid.NewGuid();
                    MatchState match = MatchState.Create(Guid.NewGuid(), playerOneId, playerTwoId, DateTimeOffset.UtcNow);
                    matches.Add(match.MatchId, match);
                    participants.Add(match.MatchId, new Dictionary<string, Guid>
                    {
                        [waitingPlayer.Subject] = playerOneId,
                        [newPlayer.Subject] = playerTwoId
                    });
                    roundService.Register(match, participants[match.MatchId]);
                    matchedMatchId = match.MatchId;
                    newPlayer.PlayerId = playerTwoId;
                    waitingPlayer.PlayerId = playerOneId;
                }
            }
        }

        if (waitingPlayer is null)
        {
            await SendStatus(newPlayer, "queued");
            return;
        }

        MatchState startedMatch;
        lock (gate) startedMatch = matches[matchedMatchId!.Value];
        object found = new
        {
            type = "match.found", version = 1, matchId = startedMatch.MatchId,
            playerIds = new[] { waitingPlayer.PlayerId, newPlayer.PlayerId }
        };
        await hubContext.Clients.Client(waitingPlayer.ConnectionId).SendAsync("match.found", found);
        await hubContext.Clients.Client(newPlayer.ConnectionId).SendAsync("match.found", found);
        object started = new
        {
            type = "match.started", version = 1, matchId = startedMatch.MatchId,
            sequence = startedMatch.SequenceNumber, roundNumber = startedMatch.RoundNumber,
            playerIds = new[] { waitingPlayer.PlayerId, newPlayer.PlayerId }
        };
        await hubContext.Clients.Client(waitingPlayer.ConnectionId).SendAsync("match.started", started);
        await hubContext.Clients.Client(newPlayer.ConnectionId).SendAsync("match.started", started);
    }

    public bool TryGetMatchParticipant(
        Guid matchId,
        string subject,
        out MatchState? match,
        out Guid playerId)
    {
        lock (gate)
        {
            match = matches.GetValueOrDefault(matchId);
            playerId = Guid.Empty;
            bool isParticipant = participants.TryGetValue(matchId, out Dictionary<string, Guid>? players) &&
                                 players.TryGetValue(subject, out playerId);
            return match is not null && isParticipant;
        }
    }

    public async Task CancelAsync(string subject, Guid requestId)
    {
        QueueEntry? entry;
        lock (gate)
        {
            entry = entries.FirstOrDefault(candidate => candidate.Subject == subject && candidate.RequestId == requestId);
            if (entry is not null) entries.Remove(entry);
        }

        if (entry is not null) await SendStatus(entry, "cancelled");
    }

    public async Task ExpireAsync(CancellationToken cancellationToken)
    {
        QueueEntry[] expired;
        lock (gate)
        {
            DateTimeOffset cutoff = DateTimeOffset.UtcNow - maximumWait;
            expired = entries.Where(entry => entry.QueuedAt <= cutoff).ToArray();
            foreach (QueueEntry entry in expired) entries.Remove(entry);
        }

        foreach (QueueEntry entry in expired)
        {
            await hubContext.Clients.Client(entry.ConnectionId).SendAsync("matchmaking.status", new
            {
                type = "matchmaking.status", version = 1, requestId = entry.RequestId,
                status = "timedOut", queuedAt = entry.QueuedAt
            }, cancellationToken);
        }
    }

    private Task SendStatus(QueueEntry entry, string status) =>
        hubContext.Clients.Client(entry.ConnectionId).SendAsync("matchmaking.status", new
        {
            type = "matchmaking.status", version = 1, requestId = entry.RequestId,
            status, queuedAt = entry.QueuedAt
        });

    private sealed class QueueEntry(string subject, string connectionId, Guid requestId, DateTimeOffset queuedAt)
    {
        public string Subject { get; } = subject;
        public string ConnectionId { get; set; } = connectionId;
        public Guid RequestId { get; set; } = requestId;
        public DateTimeOffset QueuedAt { get; } = queuedAt;
        public Guid PlayerId { get; set; }
    }
}

public sealed class MatchmakingExpirationService(MatchmakingService queue) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await queue.ExpireAsync(stoppingToken);
        }
    }
}
