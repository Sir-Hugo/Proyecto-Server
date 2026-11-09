namespace Game.Server.Contracts.V1;

public sealed record JoinMatchmakingQueueMessage(string Type, int Version, Guid RequestId);

public sealed record CancelMatchmakingQueueMessage(string Type, int Version, Guid RequestId);
