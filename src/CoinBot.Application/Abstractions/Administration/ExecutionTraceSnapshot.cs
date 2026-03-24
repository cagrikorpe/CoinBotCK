namespace CoinBot.Application.Abstractions.Administration;

public sealed record ExecutionTraceSnapshot(
    Guid Id,
    Guid? ExecutionOrderId,
    string CorrelationId,
    string ExecutionAttemptId,
    string CommandId,
    string UserId,
    string Provider,
    string Endpoint,
    string? RequestMasked,
    string? ResponseMasked,
    int? HttpStatusCode,
    string? ExchangeCode,
    int? LatencyMs,
    DateTime CreatedAtUtc);
