namespace Game.Server.Contracts.V1;

public sealed record MessageValidationIssue(string Code, string Field);
