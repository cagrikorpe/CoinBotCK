namespace CoinBot.Infrastructure.Observability;

public sealed record CorrelationContext(
    string CorrelationId,
    string RequestId,
    string TraceId,
    string? SpanId);
