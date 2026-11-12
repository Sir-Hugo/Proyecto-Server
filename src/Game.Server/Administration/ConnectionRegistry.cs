using System.Collections.Concurrent;

namespace Game.Server.Administration;

public sealed record ActiveConnection(string ConnectionId, string Subject, DateTimeOffset ConnectedAt);

public sealed class ConnectionRegistry
{
    private readonly ConcurrentDictionary<string, ActiveConnection> connections = [];
    public void Add(string connectionId, string subject) => connections[connectionId] = new(connectionId, subject, DateTimeOffset.UtcNow);
    public void Remove(string connectionId) => connections.TryRemove(connectionId, out _);
    public IReadOnlyList<ActiveConnection> List() => connections.Values.OrderBy(item => item.ConnectedAt).ToArray();
}
