using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Serilog.Context;

namespace CoinBot.Infrastructure.Observability;

public sealed class CorrelationContextMiddleware(
    RequestDelegate next,
    ICorrelationContextAccessor correlationContextAccessor,
    ILogger<CorrelationContextMiddleware> logger)
{
    private const int MaxIdentifierLength = 128;

    public async Task InvokeAsync(HttpContext context)
    {
        var currentActivity = Activity.Current;
        Activity? requestActivity = null;

        if (currentActivity is null)
        {
            requestActivity = CoinBotActivity.StartActivity("CoinBot.Request", ActivityKind.Server);
            currentActivity = Activity.Current;
        }

        var correlationId = ResolveIdentifier(
            context.Request.Headers[CorrelationHeaderNames.CorrelationId],
            static () => Guid.NewGuid().ToString("N"));
        var requestId = ResolveIdentifier(
            context.Request.Headers[CorrelationHeaderNames.RequestId],
            () => string.IsNullOrWhiteSpace(context.TraceIdentifier)
                ? Guid.NewGuid().ToString("N")
                : context.TraceIdentifier);

        context.TraceIdentifier = requestId;

        var traceId = currentActivity?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");
        var spanId = currentActivity?.SpanId.ToString();
        var correlationContext = new CorrelationContext(correlationId, requestId, traceId, spanId);

        currentActivity?.SetTag("coinbot.correlation_id", correlationId);
        currentActivity?.SetTag("coinbot.request_id", requestId);
        currentActivity?.AddBaggage("coinbot.correlation_id", correlationId);

        context.Response.Headers[CorrelationHeaderNames.CorrelationId] = correlationId;
        context.Response.Headers[CorrelationHeaderNames.RequestId] = requestId;
        context.Response.Headers[CorrelationHeaderNames.TraceId] = traceId;

        using var scope = correlationContextAccessor.BeginScope(correlationContext);
        using var correlationScope = LogContext.PushProperty("CorrelationId", correlationId);
        using var requestScope = LogContext.PushProperty("RequestId", requestId);
        using var traceScope = LogContext.PushProperty("TraceId", traceId);
        using var spanScope = LogContext.PushProperty("SpanId", spanId);

        logger.LogDebug("Correlation context established for the current request.");

        try
        {
            await next(context);
        }
        finally
        {
            requestActivity?.Stop();
        }
    }

    private static string ResolveIdentifier(StringValues headerValues, Func<string> fallbackFactory)
    {
        var candidate = headerValues.ToString().Trim();

        return IsValidIdentifier(candidate)
            ? candidate
            : fallbackFactory();
    }

    private static bool IsValidIdentifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaxIdentifierLength)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (char.IsLetterOrDigit(character) ||
                character is '-' or '_' or '.' or ':')
            {
                continue;
            }

            return false;
        }

        return true;
    }
}
