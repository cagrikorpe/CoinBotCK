namespace CoinBot.Infrastructure.Observability;

public static class CorrelationHeaderNames
{
    public const string CorrelationId = "X-Correlation-Id";

    public const string RequestId = "X-Request-Id";

    public const string TraceId = "X-Trace-Id";
}
