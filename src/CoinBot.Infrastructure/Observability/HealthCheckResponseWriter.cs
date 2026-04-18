using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace CoinBot.Infrastructure.Observability;

public static class HealthCheckResponseWriter
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] SensitiveDataKeyFragments =
    [
        "account",
        "authorization",
        "cipher",
        "cookie",
        "correlation",
        "credential",
        "email",
        "fingerprint",
        "key",
        "owner",
        "password",
        "phone",
        "request",
        "secret",
        "token",
        "trace",
        "user"
    ];

    private const int MaxDataStringLength = 256;

    public static Task WriteAsync(HttpContext context, HealthReport report)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(report);

        context.Response.ContentType = "application/json; charset=utf-8";

        var payload = new
        {
            status = report.Status.ToString(),
            checks = report.Entries.Select(entry => new
            {
                name = entry.Key,
                status = entry.Value.Status.ToString(),
                description = entry.Value.Description,
                duration = entry.Value.Duration.TotalMilliseconds,
                data = BuildSafeData(entry.Value.Data)
            })
        };

        return JsonSerializer.SerializeAsync(context.Response.Body, payload, SerializerOptions, context.RequestAborted);
    }

    private static IReadOnlyDictionary<string, object?> BuildSafeData(IReadOnlyDictionary<string, object> data)
    {
        if (data.Count == 0)
        {
            return new Dictionary<string, object?>();
        }

        var safeData = new SortedDictionary<string, object?>(StringComparer.Ordinal);

        foreach (var (key, value) in data)
        {
            if (string.IsNullOrWhiteSpace(key) || IsSensitiveKey(key))
            {
                continue;
            }

            if (TryNormalizeSafeValue(value, out var normalizedValue))
            {
                safeData[key] = normalizedValue;
            }
        }

        return safeData;
    }

    private static bool IsSensitiveKey(string key)
    {
        return SensitiveDataKeyFragments.Any(fragment =>
            key.Contains(fragment, StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryNormalizeSafeValue(object? value, out object? normalizedValue)
    {
        normalizedValue = null;

        if (value is null)
        {
            return true;
        }

        switch (value)
        {
            case string stringValue:
                normalizedValue = stringValue.Length <= MaxDataStringLength
                    ? stringValue
                    : stringValue[..MaxDataStringLength] + "...";
                return true;
            case bool:
            case byte:
            case sbyte:
            case short:
            case ushort:
            case int:
            case uint:
            case long:
            case ulong:
            case float:
            case double:
            case decimal:
            case DateTime:
            case DateTimeOffset:
                normalizedValue = value;
                return true;
        }

        var valueType = value.GetType();
        if (valueType.IsEnum)
        {
            normalizedValue = value.ToString();
            return true;
        }

        return false;
    }
}
