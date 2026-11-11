namespace Game.Server.Rooms;

public sealed class OutboxDeliveryService(MatchRoundService rounds, ILogger<OutboxDeliveryService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer=new PeriodicTimer(TimeSpan.FromSeconds(2));
        while(!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await rounds.DeliverPendingAsync(ct:stoppingToken);
                await timer.WaitForNextTickAsync(stoppingToken);
            }
            catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested) { break; }
            catch(Exception exception)
            {
                logger.LogError(exception,"Outbox delivery cycle failed");
                await Task.Delay(TimeSpan.FromSeconds(5),stoppingToken);
            }
        }
    }
}
