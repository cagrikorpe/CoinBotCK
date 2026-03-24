namespace CoinBot.Application.Abstractions.Administration;

public sealed record ExecutionTraceWriteRequest(
    string CommandId,
    string UserId,
    string Provider,
    string Endpoint,
    string? RequestMasked,
    string? ResponseMasked,
    string? CorrelationId = null,
    string? ExecutionAttemptId = null,
    Guid? ExecutionOrderId = null,
    int? HttpStatusCode = null,
    string? ExchangeCode = null,
    int? LatencyMs = null,
    DateTime? CreatedAtUtc = null);
