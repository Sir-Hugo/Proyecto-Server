using Game.Domain;
using Game.Infrastructure;
using Game.Server.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace Game.Server.Rooms;

public sealed class MatchmakingService(IHubContext<GameHub> hubContext, IConfiguration configuration, MatchRoundService roundService,
    ILogger<MatchmakingService>? logger = null)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly List<QueueEntry> entries = [];
    private readonly TimeSpan maximumWait = TimeSpan.FromMinutes(configuration.GetValue("Matchmaking:MaximumWaitMinutes", 4));

    public async Task JoinAsync(string subject, string connectionId, Guid requestId, CancellationToken ct = default)
    {
        QueueEntry? waiting = null;
        QueueEntry incoming;
        MatchState? match = null;
        List<QueueEntry> cancelled = [];
        try
        {
            await gate.WaitAsync(ct);
            try
            {
                if (await roundService.IsPlayerActiveAsync(subject, ct)) throw new HubException("player_already_in_match");
                incoming = entries.FirstOrDefault(entry => entry.Subject == subject) ?? new(subject, connectionId, requestId, DateTimeOffset.UtcNow);
                incoming.ConnectionId = connectionId;
                incoming.RequestId = requestId;
                if (!entries.Contains(incoming))
                {
                    // A queued user may have started a room match in the meantime.
                    while (entries.Count > 0 && await roundService.IsPlayerActiveAsync(entries[0].Subject, ct))
                    { cancelled.Add(entries[0]); entries.RemoveAt(0); }
                    waiting = entries.FirstOrDefault();
                    if (waiting is null) entries.Add(incoming);
                    else
                    {
                        match = MatchState.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow);
                        // Queue entries are removed only after durable creation has committed.
                        await roundService.CreateAsync(match, new Dictionary<string, Guid>
                        { [waiting.Subject] = match.FirstPlayer.PlayerId, [subject] = match.SecondPlayer.PlayerId }, ct);
                        entries.Remove(waiting);
                    }
                }
            }
            finally { gate.Release(); }
        }
        catch (PersistenceException) { throw new HubException("database_unavailable"); }
        catch (MatchOperationException exception) { throw new HubException(exception.Code); }

        foreach (var stale in cancelled) await SendStatus(stale, "cancelled");
        if (match is null) { await SendStatus(incoming, "queued"); return; }
        var found = new { type = "match.found", version = 1, matchId = match.MatchId,
            playerIds = new[] { match.FirstPlayer.PlayerId, match.SecondPlayer.PlayerId } };
        var started = new { type = "match.started", version = 1, matchId = match.MatchId,
            sequence = match.SequenceNumber, roundNumber = match.RoundNumber,
            playerIds = new[] { match.FirstPlayer.PlayerId, match.SecondPlayer.PlayerId } };
        foreach (var player in new[] { waiting!, incoming })
        {
            await DeliverAsync(player.ConnectionId, "match.found", found);
            await DeliverAsync(player.ConnectionId, "match.started", started);
        }
    }

    public async Task CancelAsync(string subject, Guid requestId, CancellationToken ct = default)
    {
        QueueEntry? entry;
        await gate.WaitAsync(ct);
        try
        {
            entry = entries.FirstOrDefault(candidate => candidate.Subject == subject && candidate.RequestId == requestId);
            if (entry is not null) entries.Remove(entry);
        }
        finally { gate.Release(); }
        if (entry is not null) await SendStatus(entry, "cancelled");
    }

    public async Task ExpireAsync(CancellationToken ct)
    {
        QueueEntry[] expired;
        await gate.WaitAsync(ct);
        try
        {
            DateTimeOffset cutoff = DateTimeOffset.UtcNow - maximumWait;
            expired = entries.Where(entry => entry.QueuedAt <= cutoff).ToArray();
            foreach (var entry in expired) entries.Remove(entry);
        }
        finally { gate.Release(); }
        foreach (var entry in expired) await SendStatus(entry, "timedOut");
    }

    private Task SendStatus(QueueEntry entry, string status) => DeliverAsync(entry.ConnectionId, "matchmaking.status",
        new { type = "matchmaking.status", version = 1, requestId = entry.RequestId, status, queuedAt = entry.QueuedAt });

    private async Task DeliverAsync(string connection, string eventName, object payload)
    {
        try { await hubContext.Clients.Client(connection).SendAsync(eventName, payload); }
        catch (Exception) { logger?.LogWarning("SignalR matchmaking delivery failed for event {Event}", eventName); }
    }
    private sealed class QueueEntry(string subject, string connectionId, Guid requestId, DateTimeOffset queuedAt)
    {
        public string Subject { get; } = subject;
        public string ConnectionId { get; set; } = connectionId;
        public Guid RequestId { get; set; } = requestId;
        public DateTimeOffset QueuedAt { get; } = queuedAt;
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
