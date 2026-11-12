using System.Globalization;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace Game.Server.Security;

public static class SecurityRateLimits
{
    public const string ApiPolicy = "game-api";
    public const string HubTransportPolicy = "game-hub-transport";

    public static IServiceCollection AddGameRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        int authenticatedRequests = Positive(configuration, "RateLimiting:Api:AuthenticatedPermitLimit", 120);
        int anonymousRequests = Positive(configuration, "RateLimiting:Api:AnonymousPermitLimit", 30);
        int apiWindowSeconds = Positive(configuration, "RateLimiting:Api:WindowSeconds", 60);
        int hubTransportRequests = Positive(configuration, "RateLimiting:Hub:TransportPermitLimit", 240);
        int hubWindowSeconds = Positive(configuration, "RateLimiting:Hub:TransportWindowSeconds", 60);

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = async (context, cancellationToken) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan retryAfter))
                    context.HttpContext.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);

                await context.HttpContext.Response.WriteAsJsonAsync(new
                {
                    type = "about:blank",
                    title = "Demasiadas solicitudes.",
                    status = StatusCodes.Status429TooManyRequests,
                    code = "rate_limit_exceeded"
                }, cancellationToken);
            };

            options.AddPolicy(ApiPolicy, context => FixedWindow(
                Partition(context),
                context.User.Identity?.IsAuthenticated == true ? authenticatedRequests : anonymousRequests,
                apiWindowSeconds));

            options.AddPolicy(HubTransportPolicy, context => FixedWindow(
                Partition(context), hubTransportRequests, hubWindowSeconds));
        });

        return services;
    }

    private static RateLimitPartition<string> FixedWindow(string key, int permits, int seconds) =>
        RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permits,
            Window = TimeSpan.FromSeconds(seconds),
            QueueLimit = 0,
            AutoReplenishment = true
        });

    private static string Partition(HttpContext context)
    {
        string? subject = context.User.FindFirstValue("sub");
        if (context.User.Identity?.IsAuthenticated == true && !string.IsNullOrWhiteSpace(subject))
            return $"user:{subject}";

        // RemoteIpAddress is the direct peer. X-Forwarded-For is deliberately not trusted.
        return $"anonymous:{context.Connection.RemoteIpAddress?.ToString() ?? "unknown"}";
    }

    private static int Positive(IConfiguration configuration, string key, int fallback)
    {
        int value = configuration.GetValue(key, fallback);
        return value > 0 ? value : throw new InvalidOperationException($"{key} must be greater than zero.");
    }
}
