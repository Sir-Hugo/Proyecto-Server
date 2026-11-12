using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;

namespace Game.Server.Security;

public sealed class HubInvocationRateLimitFilter(IConfiguration configuration) : IHubFilter
{
    private sealed class Counter
    {
        public readonly object Gate = new();
        public DateTimeOffset WindowStarted = DateTimeOffset.UtcNow;
        public int Count;
    }

    private readonly ConcurrentDictionary<string, Counter> counters = [];
    private readonly int permitLimit = Positive(configuration, "RateLimiting:Hub:InvocationPermitLimit", 60);
    private readonly TimeSpan window = TimeSpan.FromSeconds(Positive(configuration, "RateLimiting:Hub:InvocationWindowSeconds", 10));

    public ValueTask<object?> InvokeMethodAsync(HubInvocationContext invocationContext,
        Func<HubInvocationContext, ValueTask<object?>> next)
    {
        string connectionId = invocationContext.Context.ConnectionId;
        Counter counter = counters.GetOrAdd(connectionId, _ => new Counter());
        DateTimeOffset now = DateTimeOffset.UtcNow;
        lock (counter.Gate)
        {
            if (now - counter.WindowStarted >= window)
            {
                counter.WindowStarted = now;
                counter.Count = 0;
            }

            counter.Count++;
            if (counter.Count > permitLimit)
                throw new HubException("rate_limit_exceeded");
        }

        return next(invocationContext);
    }

    public ValueTask OnDisconnectedAsync(HubLifetimeContext context, Exception? exception,
        Func<HubLifetimeContext, Exception?, ValueTask> next)
    {
        counters.TryRemove(context.Context.ConnectionId, out _);
        return next(context, exception);
    }

    private static int Positive(IConfiguration configuration, string key, int fallback)
    {
        int value = configuration.GetValue(key, fallback);
        return value > 0 ? value : throw new InvalidOperationException($"{key} must be greater than zero.");
    }
}
