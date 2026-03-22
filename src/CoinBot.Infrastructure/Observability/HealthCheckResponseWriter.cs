using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace CoinBot.Infrastructure.Observability;

public static class HealthCheckResponseWriter
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public static Task WriteAsync(HttpContext context, HealthReport report)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(report);

        context.Response.ContentType = "application/json; charset=utf-8";

        var correlationContext = context.RequestServices
            .GetService<ICorrelationContextAccessor>()
            ?.Current;

        var payload = new
        {
            status = report.Status.ToString(),
            correlationId = correlationContext?.CorrelationId,
            requestId = correlationContext?.RequestId ?? context.TraceIdentifier,
            traceId = correlationContext?.TraceId ?? Activity.Current?.TraceId.ToString(),
            checks = report.Entries.Select(entry => new
            {
                name = entry.Key,
                status = entry.Value.Status.ToString(),
                description = entry.Value.Description,
                duration = entry.Value.Duration.TotalMilliseconds,
                data = entry.Value.Data.ToDictionary(dataEntry => dataEntry.Key, dataEntry => dataEntry.Value)
            })
        };

        return JsonSerializer.SerializeAsync(context.Response.Body, payload, SerializerOptions, context.RequestAborted);
    }
}
