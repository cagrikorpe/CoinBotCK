using System.Diagnostics;
using CoinBot.Application.Abstractions.Administration;
using CoinBot.Domain.Entities;
using CoinBot.Infrastructure.Observability;
using CoinBot.Infrastructure.Persistence;

namespace CoinBot.Infrastructure.Administration;

public sealed class AdminAuditLogService(
    ApplicationDbContext dbContext,
    ICorrelationContextAccessor correlationContextAccessor,
    TimeProvider timeProvider) : IAdminAuditLogService
{
    public async Task WriteAsync(AdminAuditLogWriteRequest request, CancellationToken cancellationToken = default)
    {
        var auditLog = new AdminAuditLog
        {
            Id = Guid.NewGuid(),
            ActorUserId = NormalizeRequired(request.ActorUserId, 450, nameof(request.ActorUserId)),
            ActionType = NormalizeRequired(request.ActionType, 128, nameof(request.ActionType)),
            TargetType = NormalizeRequired(request.TargetType, 128, nameof(request.TargetType)),
            TargetId = NormalizeOptional(request.TargetId, 256),
            OldValueSummary = NormalizeOptional(request.OldValueSummary, 2048),
            NewValueSummary = NormalizeOptional(request.NewValueSummary, 2048),
            Reason = NormalizeRequired(request.Reason, 512, nameof(request.Reason)),
            IpAddress = NormalizeOptional(request.IpAddress, 128),
            UserAgent = NormalizeOptional(request.UserAgent, 256),
            CorrelationId = NormalizeOptional(ResolveCorrelationId(request.CorrelationId), 128),
            CreatedAtUtc = timeProvider.GetUtcNow().UtcDateTime
        };

        dbContext.AdminAuditLogs.Add(auditLog);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private string ResolveCorrelationId(string? correlationId)
    {
        var normalizedCorrelationId = correlationId?.Trim();

        if (!string.IsNullOrWhiteSpace(normalizedCorrelationId))
        {
            return normalizedCorrelationId;
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

        return normalizedValue.Length <= maxLength
            ? normalizedValue
            : throw new ArgumentOutOfRangeException(parameterName, $"The value cannot exceed {maxLength} characters.");
    }

    private static string? NormalizeOptional(string? value, int maxLength)
    {
        var normalizedValue = value?.Trim();

        if (string.IsNullOrWhiteSpace(normalizedValue))
        {
            return null;
        }

        return normalizedValue.Length <= maxLength
            ? normalizedValue
            : throw new ArgumentOutOfRangeException(nameof(value), $"The value cannot exceed {maxLength} characters.");
    }
}
