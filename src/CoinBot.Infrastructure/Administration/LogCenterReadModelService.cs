using CoinBot.Application.Abstractions.Administration;
using CoinBot.Domain.Entities;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Linq.Expressions;

namespace CoinBot.Infrastructure.Administration;

public sealed partial class LogCenterReadModelService(
    ApplicationDbContext dbContext,
    IOptions<LogCenterRetentionOptions> retentionOptions,
    ILogger<LogCenterReadModelService> logger) : ILogCenterReadModelService
{
    private const int LatestPreviewWindowHours = 6;
    private const int LatestPreviewSourceTakeCap = 120;
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
            var useLatestPreviewMode = ShouldUseLatestPreviewMode(normalized);
            var effectiveFilters = useLatestPreviewMode
                ? ApplyLatestPreviewWindow(normalized)
                : normalized;
            var sourceTake = useLatestPreviewMode
                ? Math.Clamp(effectiveFilters.Take, effectiveFilters.Take, LatestPreviewSourceTakeCap)
                : Math.Clamp(effectiveFilters.Take * 3, effectiveFilters.Take, 1000);

            var decisionQuery = ApplyDecisionFilters(dbContext.DecisionTraces.AsNoTracking().IgnoreQueryFilters().Where(entity => !entity.IsDeleted), effectiveFilters);
            var executionQuery = ApplyExecutionFilters(dbContext.ExecutionTraces.AsNoTracking().IgnoreQueryFilters().Where(entity => !entity.IsDeleted), effectiveFilters);
            var adminAuditQuery = ApplyAdminAuditFilters(dbContext.AdminAuditLogs.AsNoTracking(), effectiveFilters);
            var incidentQuery = ApplyIncidentFilters(dbContext.Incidents.AsNoTracking().IgnoreQueryFilters().Where(entity => !entity.IsDeleted), effectiveFilters);
            var incidentEventQuery = ApplyIncidentEventFilters(dbContext.IncidentEvents.AsNoTracking().IgnoreQueryFilters().Where(entity => !entity.IsDeleted), effectiveFilters);
            var approvalQueueQuery = ApplyApprovalQueueFilters(dbContext.ApprovalQueues.AsNoTracking().IgnoreQueryFilters().Where(entity => !entity.IsDeleted), effectiveFilters);
            var approvalActionQuery = ApplyApprovalActionFilters(dbContext.ApprovalActions.AsNoTracking().IgnoreQueryFilters().Where(entity => !entity.IsDeleted), effectiveFilters);

            List<DecisionTrace> decisionRows;
            int decisionCount;
            List<ExecutionTrace> executionRows;
            int executionCount;
            List<AdminAuditLog> adminAuditRows;
            int adminAuditCount;
            List<Incident> incidentRows;
            int incidentCount;
            List<IncidentEvent> incidentEventRows;
            int incidentEventCount;
            List<ApprovalQueue> approvalQueueRows;
            int approvalQueueCount;
            List<ApprovalAction> approvalActionRows;
            int approvalActionCount;

            if (useLatestPreviewMode)
            {
                decisionRows = await LoadRowsAsync(decisionQuery, entity => entity.CreatedAtUtc, sourceTake, cancellationToken);
                executionRows = await LoadRowsAsync(executionQuery, entity => entity.CreatedAtUtc, sourceTake, cancellationToken);
                adminAuditRows = await LoadRowsAsync(adminAuditQuery, entity => entity.CreatedAtUtc, sourceTake, cancellationToken);
                incidentRows = await LoadRowsAsync(incidentQuery, entity => entity.CreatedDate, sourceTake, cancellationToken);
                incidentEventRows = await LoadRowsAsync(incidentEventQuery, entity => entity.CreatedDate, sourceTake, cancellationToken);
                approvalQueueRows = await LoadRowsAsync(approvalQueueQuery, entity => entity.CreatedDate, sourceTake, cancellationToken);
                approvalActionRows = await LoadRowsAsync(approvalActionQuery, entity => entity.CreatedDate, sourceTake, cancellationToken);

                decisionCount = decisionRows.Count;
                executionCount = executionRows.Count;
                adminAuditCount = adminAuditRows.Count;
                incidentCount = incidentRows.Count;
                incidentEventCount = incidentEventRows.Count;
                approvalQueueCount = approvalQueueRows.Count;
                approvalActionCount = approvalActionRows.Count;
            }
            else
            {
                (decisionRows, decisionCount) = await LoadRowsWithCountAsync(
                    decisionQuery,
                    entity => entity.CreatedAtUtc,
                    sourceTake,
                    cancellationToken);
                (executionRows, executionCount) = await LoadRowsWithCountAsync(
                    executionQuery,
                    entity => entity.CreatedAtUtc,
                    sourceTake,
                    cancellationToken);
                (adminAuditRows, adminAuditCount) = await LoadRowsWithCountAsync(
                    adminAuditQuery,
                    entity => entity.CreatedAtUtc,
                    sourceTake,
                    cancellationToken);
                (incidentRows, incidentCount) = await LoadRowsWithCountAsync(
                    incidentQuery,
                    entity => entity.CreatedDate,
                    sourceTake,
                    cancellationToken);
                (incidentEventRows, incidentEventCount) = await LoadRowsWithCountAsync(
                    incidentEventQuery,
                    entity => entity.CreatedDate,
                    sourceTake,
                    cancellationToken);
                (approvalQueueRows, approvalQueueCount) = await LoadRowsWithCountAsync(
                    approvalQueueQuery,
                    entity => entity.CreatedDate,
                    sourceTake,
                    cancellationToken);
                (approvalActionRows, approvalActionCount) = await LoadRowsWithCountAsync(
                    approvalActionQuery,
                    entity => entity.CreatedDate,
                    sourceTake,
                    cancellationToken);
            }

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
                effectiveFilters.ToRequest(),
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
                .Select(entity => new RetentionAuditLogSnapshot(
                    entity.CreatedAtUtc,
                    entity.ActionType,
                    entity.NewValueSummary,
                    entity.OldValueSummary,
                    entity.Reason))
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

    private static async Task<(List<TEntity> Rows, int TotalCount)> LoadRowsWithCountAsync<TEntity>(
        IQueryable<TEntity> query,
        Expression<Func<TEntity, DateTime>> createdAtSelector,
        int take,
        CancellationToken cancellationToken)
        where TEntity : class
    {
        var rowsWithCount = await query
            .OrderByDescending(createdAtSelector)
            .Select(entity => new CountedQueryRow<TEntity>(entity, query.Count()))
            .Take(take)
            .ToListAsync(cancellationToken);

        if (rowsWithCount.Count == 0)
        {
            return ([], 0);
        }

        return (rowsWithCount.Select(item => item.Entity).ToList(), rowsWithCount[0].TotalCount);
    }

    private static async Task<List<TEntity>> LoadRowsAsync<TEntity>(
        IQueryable<TEntity> query,
        Expression<Func<TEntity, DateTime>> createdAtSelector,
        int take,
        CancellationToken cancellationToken)
        where TEntity : class
    {
        return await query
            .OrderByDescending(createdAtSelector)
            .Take(take)
            .ToListAsync(cancellationToken);
    }

    private static bool ShouldUseLatestPreviewMode(NormalizedLogCenterQuery normalized)
    {
        return !normalized.HasSearchFilters && !normalized.HasDateRange;
    }

    private static NormalizedLogCenterQuery ApplyLatestPreviewWindow(NormalizedLogCenterQuery normalized)
    {
        var utcNow = DateTime.UtcNow;
        return normalized.WithDateRange(utcNow.AddHours(-LatestPreviewWindowHours), utcNow);
    }

    private sealed record CountedQueryRow<TEntity>(TEntity Entity, int TotalCount)
        where TEntity : class;

    private sealed record RetentionAuditLogSnapshot(
        DateTime CreatedAtUtc,
        string ActionType,
        string? NewValueSummary,
        string? OldValueSummary,
        string Reason);
}
