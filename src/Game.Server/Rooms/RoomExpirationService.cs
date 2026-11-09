namespace Game.Server.Rooms;

/// <summary>
/// Servicio en segundo plano que comprueba periódicamente si existen salas inactivas y las elimina del servicio de gestión de salas en memoria.
/// </summary>
public sealed class RoomExpirationService : BackgroundService
{
    private readonly InMemoryRoomService roomService;
    private readonly ILogger<RoomExpirationService> logger;
    private readonly TimeSpan inactivityLimit;
    private readonly TimeSpan cleanupInterval;

    public RoomExpirationService(
        InMemoryRoomService roomService,
        IConfiguration configuration,
        ILogger<RoomExpirationService> logger)
    {
        this.roomService = roomService;
        this.logger = logger;

        int inactivityMinutes = configuration.GetValue("RoomLifecycle:InactivityLimitMinutes", 5);
        int cleanupSeconds = configuration.GetValue("RoomLifecycle:CleanupIntervalSeconds", 30);
        if (inactivityMinutes <= 0 || cleanupSeconds <= 0)
        {
            throw new InvalidOperationException(
                "Room lifecycle durations must be greater than zero.");
        }

        inactivityLimit = TimeSpan.FromMinutes(inactivityMinutes);
        cleanupInterval = TimeSpan.FromSeconds(cleanupSeconds);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            int removedCount = roomService.RemoveInactiveRooms(DateTimeOffset.UtcNow, inactivityLimit);
            if (removedCount > 0)
            {
                logger.LogInformation("Removed {RoomCount} inactive rooms.", removedCount);
            }

            await Task.Delay(cleanupInterval, stoppingToken);
        }
    }
}
