using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Caching.Memory;

namespace Game.Server.Security;

public sealed class HubInvocationRateLimitFilter(IConfiguration configuration, IMemoryCache cache) : IHubFilter
{
    private sealed class Counter
    {
        public readonly object Gate = new();
        public DateTimeOffset WindowStarted = DateTimeOffset.UtcNow;
        public int Count;
    }

    private readonly ConcurrentDictionary<string, Counter> counters = [];
    private readonly int permitLimit = Positive(configuration, "RateLimiting:Hub:InvocationPermitLimit", 60);
    private readonly int userPermitLimit = Positive(configuration, "RateLimiting:Hub:UserInvocationPermitLimit", 120);
    private readonly TimeSpan window = TimeSpan.FromSeconds(Positive(configuration, "RateLimiting:Hub:InvocationWindowSeconds", 10));

    public ValueTask<object?> InvokeMethodAsync(HubInvocationContext invocationContext,
        Func<HubInvocationContext, ValueTask<object?>> next)
    {
        string connectionId = invocationContext.Context.ConnectionId;
        Counter connectionCounter = counters.GetOrAdd(connectionId, _ => new Counter());
        if (!TryAcquire(connectionCounter, permitLimit)) throw new HubException("rate_limit_exceeded");

        string? subject = invocationContext.Context.User?.FindFirst("sub")?.Value;
        if (string.IsNullOrWhiteSpace(subject)) throw new HubException("human_user_token_required");
        Counter userCounter = cache.GetOrCreate($"hub-rate:{subject}", entry =>
        {
            entry.SlidingExpiration = window + window;
            return new Counter();
        })!;
        if (!TryAcquire(userCounter, userPermitLimit)) throw new HubException("rate_limit_exceeded");

        return next(invocationContext);
    }

    private bool TryAcquire(Counter counter, int limit)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        lock (counter.Gate)
        {
            if (now - counter.WindowStarted >= window)
            {
                counter.WindowStarted = now;
                counter.Count = 0;
            }
            counter.Count++;
            return counter.Count <= limit;
        }
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
