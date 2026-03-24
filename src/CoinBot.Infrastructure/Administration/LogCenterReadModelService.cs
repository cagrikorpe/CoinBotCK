using CoinBot.Application.Abstractions.Administration;
using CoinBot.Domain.Entities;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CoinBot.Infrastructure.Administration;

public sealed partial class LogCenterReadModelService(
    ApplicationDbContext dbContext,
    IOptions<LogCenterRetentionOptions> retentionOptions,
    ILogger<LogCenterReadModelService> logger) : ILogCenterReadModelService
{
    private const string RetentionStartedAction = "LogCenter.Retention.Started";
    private const string RetentionCompletedAction = "LogCenter.Retention.Completed";
    private const string RetentionFailedAction = "LogCenter.Retention.Failed";

    public async Task<LogCenterPageSnapshot> GetPageAsync(
        LogCenterQueryRequest request,
        CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(request);

        if (normalized.HasInvalidDateRange)
        {
            return new LogCenterPageSnapshot(
                normalized.ToRequest(),
                new LogCenterSummarySnapshot(0, 0, 0, 0, 0, 0, 0, 0, 0, null),
                await BuildRetentionSnapshotAsync(cancellationToken),
                Array.Empty<LogCenterEntrySnapshot>(),
                true,
                "Date range is invalid.");
        }

        try
        {
            var sourceTake = Math.Clamp(normalized.Take * 3, normalized.Take, 1000);

            var decisionQuery = ApplyDecisionFilters(dbContext.DecisionTraces.AsNoTracking().IgnoreQueryFilters().Where(entity => !entity.IsDeleted), normalized);
            var executionQuery = ApplyExecutionFilters(dbContext.ExecutionTraces.AsNoTracking().IgnoreQueryFilters().Where(entity => !entity.IsDeleted), normalized);
            var adminAuditQuery = ApplyAdminAuditFilters(dbContext.AdminAuditLogs.AsNoTracking(), normalized);
            var incidentQuery = ApplyIncidentFilters(dbContext.Incidents.AsNoTracking().IgnoreQueryFilters().Where(entity => !entity.IsDeleted), normalized);
            var incidentEventQuery = ApplyIncidentEventFilters(dbContext.IncidentEvents.AsNoTracking().IgnoreQueryFilters().Where(entity => !entity.IsDeleted), normalized);
            var approvalQueueQuery = ApplyApprovalQueueFilters(dbContext.ApprovalQueues.AsNoTracking().IgnoreQueryFilters().Where(entity => !entity.IsDeleted), normalized);
            var approvalActionQuery = ApplyApprovalActionFilters(dbContext.ApprovalActions.AsNoTracking().IgnoreQueryFilters().Where(entity => !entity.IsDeleted), normalized);

            var decisionCount = await decisionQuery.CountAsync(cancellationToken);
            var executionCount = await executionQuery.CountAsync(cancellationToken);
            var adminAuditCount = await adminAuditQuery.CountAsync(cancellationToken);
            var incidentCount = await incidentQuery.CountAsync(cancellationToken);
            var incidentEventCount = await incidentEventQuery.CountAsync(cancellationToken);
            var approvalQueueCount = await approvalQueueQuery.CountAsync(cancellationToken);
            var approvalActionCount = await approvalActionQuery.CountAsync(cancellationToken);

            var decisionRows = await decisionQuery.OrderByDescending(entity => entity.CreatedAtUtc).Take(sourceTake).ToListAsync(cancellationToken);
            var executionRows = await executionQuery.OrderByDescending(entity => entity.CreatedAtUtc).Take(sourceTake).ToListAsync(cancellationToken);
            var adminAuditRows = await adminAuditQuery.OrderByDescending(entity => entity.CreatedAtUtc).Take(sourceTake).ToListAsync(cancellationToken);
            var incidentRows = await incidentQuery.OrderByDescending(entity => entity.CreatedDate).Take(sourceTake).ToListAsync(cancellationToken);
            var incidentEventRows = await incidentEventQuery.OrderByDescending(entity => entity.CreatedDate).Take(sourceTake).ToListAsync(cancellationToken);
            var approvalQueueRows = await approvalQueueQuery.OrderByDescending(entity => entity.CreatedDate).Take(sourceTake).ToListAsync(cancellationToken);
            var approvalActionRows = await approvalActionQuery.OrderByDescending(entity => entity.CreatedDate).Take(sourceTake).ToListAsync(cancellationToken);

            var entries = new List<LogCenterEntrySnapshot>(
                decisionRows.Count +
                executionRows.Count +
                adminAuditRows.Count +
                incidentRows.Count +
                incidentEventRows.Count +
                approvalQueueRows.Count +
                approvalActionRows.Count);

            entries.AddRange(decisionRows.Select(MapDecisionTrace));
            entries.AddRange(executionRows.Select(MapExecutionTrace));
            entries.AddRange(adminAuditRows.Select(MapAdminAuditLog));
            entries.AddRange(incidentRows.Select(MapIncident));
            entries.AddRange(incidentEventRows.Select(MapIncidentEvent));
            entries.AddRange(approvalQueueRows.Select(MapApprovalQueue));
            entries.AddRange(approvalActionRows.Select(MapApprovalAction));

            var orderedEntries = entries
                .OrderByDescending(entry => entry.CreatedAtUtc)
                .ThenBy(entry => entry.Kind, StringComparer.Ordinal)
                .ThenBy(entry => entry.Reference, StringComparer.Ordinal)
                .Take(normalized.Take)
                .ToArray();

            var summary = new LogCenterSummarySnapshot(
                decisionCount + executionCount + adminAuditCount + incidentCount + incidentEventCount + approvalQueueCount + approvalActionCount,
                decisionCount,
                executionCount,
                adminAuditCount,
                incidentCount,
                incidentEventCount,
                approvalQueueCount,
                approvalActionCount,
                orderedEntries.Count(IsCritical),
                orderedEntries.FirstOrDefault()?.CreatedAtUtc);

            return new LogCenterPageSnapshot(
                normalized.ToRequest(),
                summary,
                await BuildRetentionSnapshotAsync(cancellationToken),
                orderedEntries,
                false,
                null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Log center page query failed.");

            return new LogCenterPageSnapshot(
                normalized.ToRequest(),
                new LogCenterSummarySnapshot(0, 0, 0, 0, 0, 0, 0, 0, 0, null),
                await BuildRetentionSnapshotAsync(cancellationToken),
                Array.Empty<LogCenterEntrySnapshot>(),
                true,
                "Log center verisi alınamadı.");
        }
    }

    private async Task<LogCenterRetentionSnapshot> BuildRetentionSnapshotAsync(CancellationToken cancellationToken)
    {
        try
        {
            var optionsValue = retentionOptions.Value;
            var retentionActions = new[]
            {
                RetentionStartedAction,
                RetentionCompletedAction,
                RetentionFailedAction
            };

            var lastRun = await dbContext.AdminAuditLogs
                .AsNoTracking()
                .Where(entity => retentionActions.Contains(entity.ActionType))
                .OrderByDescending(entity => entity.CreatedAtUtc)
                .FirstOrDefaultAsync(cancellationToken);

            return new LogCenterRetentionSnapshot(
                optionsValue.Enabled,
                optionsValue.DecisionTraceRetentionDays,
                optionsValue.ExecutionTraceRetentionDays,
                optionsValue.AdminAuditLogRetentionDays,
                optionsValue.IncidentRetentionDays,
                optionsValue.ApprovalRetentionDays,
                optionsValue.BatchSize,
                lastRun?.CreatedAtUtc,
                lastRun is null
                    ? null
                    : $"{lastRun.ActionType} | {lastRun.NewValueSummary ?? lastRun.OldValueSummary ?? lastRun.Reason}");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Log center retention snapshot could not be loaded.");
            var optionsValue = retentionOptions.Value;

            return new LogCenterRetentionSnapshot(
                optionsValue.Enabled,
                optionsValue.DecisionTraceRetentionDays,
                optionsValue.ExecutionTraceRetentionDays,
                optionsValue.AdminAuditLogRetentionDays,
                optionsValue.IncidentRetentionDays,
                optionsValue.ApprovalRetentionDays,
                optionsValue.BatchSize,
                null,
                null);
        }
    }
}
