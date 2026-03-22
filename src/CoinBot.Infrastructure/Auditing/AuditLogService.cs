using System.Diagnostics;
using CoinBot.Application.Abstractions.Auditing;
using CoinBot.Domain.Entities;
using CoinBot.Infrastructure.Observability;
using CoinBot.Infrastructure.Persistence;

namespace CoinBot.Infrastructure.Auditing;

public sealed class AuditLogService(
    ApplicationDbContext dbContext,
    ICorrelationContextAccessor correlationContextAccessor) : IAuditLogService
{
    public async Task WriteAsync(AuditLogWriteRequest request, CancellationToken cancellationToken = default)
    {
        var auditLog = new AuditLog
        {
            Actor = NormalizeRequired(request.Actor, 256, nameof(request.Actor)),
            Action = NormalizeRequired(request.Action, 128, nameof(request.Action)),
            Target = NormalizeRequired(request.Target, 256, nameof(request.Target)),
            Context = NormalizeOptional(request.Context, 2048),
            CorrelationId = NormalizeRequired(ResolveCorrelationId(request.CorrelationId), 128, nameof(request.CorrelationId)),
            Outcome = NormalizeRequired(request.Outcome, 128, nameof(request.Outcome)),
            Environment = NormalizeRequired(request.Environment, 32, nameof(request.Environment))
        };

        dbContext.AuditLogs.Add(auditLog);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private string ResolveCorrelationId(string? correlationId)
    {
        if (!string.IsNullOrWhiteSpace(correlationId))
        {
            return correlationId.Trim();
        }

        var scopedCorrelationId = correlationContextAccessor.Current?.CorrelationId;

        if (!string.IsNullOrWhiteSpace(scopedCorrelationId))
        {
            return scopedCorrelationId;
        }

        var activityCorrelationId = Activity.Current?.TraceId.ToString();

        if (!string.IsNullOrWhiteSpace(activityCorrelationId))
        {
            return activityCorrelationId;
        }

        return Guid.NewGuid().ToString("N");
    }

    private static string NormalizeRequired(string? value, int maxLength, string parameterName)
    {
        var normalizedValue = value?.Trim();

        if (string.IsNullOrWhiteSpace(normalizedValue))
        {
            throw new ArgumentException("The value is required.", parameterName);
        }

        if (normalizedValue.Length > maxLength)
        {
            throw new ArgumentOutOfRangeException(parameterName, $"The value cannot exceed {maxLength} characters.");
        }

        return normalizedValue;
    }

    private static string? NormalizeOptional(string? value, int maxLength)
    {
        var normalizedValue = value?.Trim();

        if (string.IsNullOrWhiteSpace(normalizedValue))
        {
            return null;
        }

        if (normalizedValue.Length > maxLength)
        {
            throw new ArgumentOutOfRangeException(nameof(value), $"The value cannot exceed {maxLength} characters.");
        }

        return normalizedValue;
    }
}
