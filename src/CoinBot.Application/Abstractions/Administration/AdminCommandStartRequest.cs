namespace CoinBot.Application.Abstractions.Administration;

public sealed record AdminCommandStartRequest(
    string CommandId,
    string CommandType,
    string ActorUserId,
    string ScopeKey,
    string PayloadHash,
    string? CorrelationId);
