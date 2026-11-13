using Npgsql;

namespace Game.Server.Observability;

public sealed class GameMetricsCollector(NpgsqlDataSource source, GameTelemetry telemetry,
    ILogger<GameMetricsCollector> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(15));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using NpgsqlCommand command = source.CreateCommand("SELECT count(*) FROM matches WHERE status='active'");
                object? result = await command.ExecuteScalarAsync(stoppingToken);
                telemetry.SetActiveMatches(Convert.ToInt64(result));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (PostgresException exception)
            {
                telemetry.RecordFailure("active_matches_collection", "postgresql");
                logger.LogWarning("Active-match metric collection failed with SQLSTATE {SqlState}", exception.SqlState);
            }
            catch (NpgsqlException)
            {
                telemetry.RecordFailure("active_matches_collection", "postgresql");
                logger.LogWarning("Active-match metric collection failed because PostgreSQL is unavailable");
            }
            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken)) break;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
