using System.Collections.Concurrent;
using Game.Server.Observability;

namespace Game.Server.Administration;

public sealed record ActiveConnection(string ConnectionId, string Subject, DateTimeOffset ConnectedAt);

public sealed class ConnectionRegistry
{
    private readonly ConcurrentDictionary<string, ActiveConnection> connections = [];
    private readonly object gate = new();
    private readonly int maximumConnectionsPerUser;
    private readonly GameTelemetry? telemetry;
    private readonly ILogger<ConnectionRegistry>? logger;

    public ConnectionRegistry(IConfiguration configuration, GameTelemetry? telemetry = null, ILogger<ConnectionRegistry>? logger = null)
    {
        this.telemetry = telemetry;
        this.logger = logger;
        maximumConnectionsPerUser = configuration.GetValue("RateLimiting:Hub:MaximumConnectionsPerUser", 5);
        if (maximumConnectionsPerUser <= 0)
            throw new InvalidOperationException("RateLimiting:Hub:MaximumConnectionsPerUser must be greater than zero.");
    }

    public bool TryAdd(string connectionId, string subject)
    {
        lock (gate)
        {
            if (connections.Values.Count(item => item.Subject == subject) >= maximumConnectionsPerUser)
            {
                telemetry?.RecordFailure("signalr_connect", "connection_limit");
                return false;
            }
            connections[connectionId] = new(connectionId, subject, DateTimeOffset.UtcNow);
            telemetry?.SetActiveConnections(connections.Count);
            logger?.LogInformation("SignalR connection registered; active connections {ActiveConnections}", connections.Count);
            return true;
        }
    }
    public void Remove(string connectionId)
    {
        if (!connections.TryRemove(connectionId, out _)) return;
        telemetry?.SetActiveConnections(connections.Count);
        logger?.LogInformation("SignalR connection removed; active connections {ActiveConnections}", connections.Count);
    }
    public IReadOnlyList<ActiveConnection> List() => connections.Values.OrderBy(item => item.ConnectedAt).ToArray();
}
