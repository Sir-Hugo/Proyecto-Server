using Game.Domain;

namespace Game.Server.Rooms;

/// <summary>Finaliza las partidas que superan el plazo acordado de inactividad.</summary>
public sealed class MatchExpirationService(
    MatchRoundService matches,
    IConfiguration configuration,
    ILogger<MatchExpirationService> logger) : BackgroundService
{
    private readonly TimeSpan inactivityLimit = TimeSpan.FromMinutes(
        configuration.GetValue("MatchLifecycle:InactivityLimitMinutes", 5));
    private readonly TimeSpan checkInterval = TimeSpan.FromSeconds(
        configuration.GetValue("MatchLifecycle:CheckIntervalSeconds", 5));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(checkInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            int notifiedPlayers = await matches.ExpireInactiveMatchesAsync(
                DateTimeOffset.UtcNow, inactivityLimit, stoppingToken);
            if (notifiedPlayers > 0)
                logger.LogInformation("Ended inactive matches and notified {PlayerCount} players.", notifiedPlayers);
        }
    }
}
