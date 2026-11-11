namespace Game.Infrastructure;

// Safe to report: provider messages, connection strings and payloads are deliberately omitted.
public sealed class PersistenceException(string operation, string? sqlState = null)
    : Exception($"PostgreSQL operation '{operation}' failed.")
{
    public string? SqlState { get; } = sqlState;
}
