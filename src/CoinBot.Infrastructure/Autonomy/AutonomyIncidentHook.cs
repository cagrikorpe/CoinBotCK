using CoinBot.Application.Abstractions.Administration;
using CoinBot.Application.Abstractions.Autonomy;

namespace CoinBot.Infrastructure.Autonomy;

public sealed class AutonomyIncidentHook(
    IAdminAuditLogService adminAuditLogService) : IAutonomyIncidentHook
{
    public Task WriteIncidentAsync(
        AutonomyIncidentHookRequest request,
        CancellationToken cancellationToken = default)
    {
        return adminAuditLogService.WriteAsync(
            new AdminAuditLogWriteRequest(
                request.ActorUserId,
                "Admin.Autonomy.Incident",
                "Autonomy",
                request.Scope,
                OldValueSummary: null,
                NewValueSummary: Truncate(request.Detail, 2048),
                Reason: Truncate(request.Summary, 512) ?? "Autonomy incident",
                IpAddress: null,
                UserAgent: null,
                request.CorrelationId),
            cancellationToken);
    }

    public Task WriteRecoveryAsync(
        AutonomyRecoveryHookRequest request,
        CancellationToken cancellationToken = default)
    {
        return adminAuditLogService.WriteAsync(
            new AdminAuditLogWriteRequest(
                request.ActorUserId,
                "Admin.Autonomy.Recovery",
                "Autonomy",
                request.Scope,
                OldValueSummary: null,
                NewValueSummary: Truncate(request.Summary, 2048),
                Reason: "Autonomy recovery summary",
                IpAddress: null,
                UserAgent: null,
                request.CorrelationId),
            cancellationToken);
    }

    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalizedValue = value.Trim();
        return normalizedValue.Length <= maxLength
            ? normalizedValue
            : normalizedValue[..maxLength];
    }
}
