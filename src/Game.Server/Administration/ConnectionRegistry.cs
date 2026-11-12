using System.Collections.Concurrent;

namespace Game.Server.Administration;

public sealed record ActiveConnection(string ConnectionId, string Subject, DateTimeOffset ConnectedAt);

public sealed class ConnectionRegistry
{
    private readonly ConcurrentDictionary<string, ActiveConnection> connections = [];
    private readonly object gate = new();
    private readonly int maximumConnectionsPerUser;

    public ConnectionRegistry(IConfiguration configuration)
    {
        maximumConnectionsPerUser = configuration.GetValue("RateLimiting:Hub:MaximumConnectionsPerUser", 5);
        if (maximumConnectionsPerUser <= 0)
            throw new InvalidOperationException("RateLimiting:Hub:MaximumConnectionsPerUser must be greater than zero.");
    }

    public bool TryAdd(string connectionId, string subject)
    {
        lock (gate)
        {
            if (connections.Values.Count(item => item.Subject == subject) >= maximumConnectionsPerUser)
                return false;
            connections[connectionId] = new(connectionId, subject, DateTimeOffset.UtcNow);
            return true;
        }
    }
    public void Remove(string connectionId) => connections.TryRemove(connectionId, out _);
    public IReadOnlyList<ActiveConnection> List() => connections.Values.OrderBy(item => item.ConnectedAt).ToArray();
}
