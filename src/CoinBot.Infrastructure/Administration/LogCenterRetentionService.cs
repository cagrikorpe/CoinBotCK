using System.Linq.Expressions;
using CoinBot.Application.Abstractions.Administration;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CoinBot.Infrastructure.Administration;

public sealed class LogCenterRetentionService(
    ApplicationDbContext dbContext,
    IAdminAuditLogService adminAuditLogService,
    IOptions<LogCenterRetentionOptions> retentionOptions,
    TimeProvider timeProvider,
    ILogger<LogCenterRetentionService> logger) : ILogCenterRetentionService
{
    private const string RetentionStartedAction = "LogCenter.Retention.Started";
    private const string RetentionCompletedAction = "LogCenter.Retention.Completed";
    private const string RetentionFailedAction = "LogCenter.Retention.Failed";
    private readonly LogCenterRetentionOptions optionsValue = retentionOptions.Value;

    public async Task<LogCenterRetentionSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await BuildSnapshotAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Retention snapshot could not be loaded.");

            return CreateFallbackSnapshot(null, null);
        }
    }

    public async Task<LogCenterRetentionRunSnapshot> ApplyAsync(CancellationToken cancellationToken = default)
    {
        if (!optionsValue.Enabled)
        {
            logger.LogInformation("Log center retention is disabled.");

            var now = timeProvider.GetUtcNow().UtcDateTime;
            return new LogCenterRetentionRunSnapshot(now, now, 0, 0, 0, 0, 0, 0, 0);
        }

        var startedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
        var runCorrelationId = $"log-retention:{startedAtUtc:yyyyMMddHHmmss}";
        var targetSummary = "DecisionTrace|ExecutionTrace|AdminAuditLog|Incident|ApprovalQueue";
        await adminAuditLogService.WriteAsync(
            new AdminAuditLogWriteRequest(
                "system:log-retention",
                RetentionStartedAction,
                "LogCenterRetention",
                targetSummary,
                BuildRetentionPolicySummary(),
                null,
                "Retention run started.",
                null,
                null,
                runCorrelationId),
            cancellationToken);

        try
        {
            var decisionCutoffUtc = startedAtUtc.AddDays(-optionsValue.DecisionTraceRetentionDays);
            var executionCutoffUtc = startedAtUtc.AddDays(-optionsValue.ExecutionTraceRetentionDays);
            var adminAuditCutoffUtc = startedAtUtc.AddDays(-optionsValue.AdminAuditLogRetentionDays);
            var incidentCutoffUtc = startedAtUtc.AddDays(-optionsValue.IncidentRetentionDays);
            var approvalCutoffUtc = startedAtUtc.AddDays(-optionsValue.ApprovalRetentionDays);

            var decisionTraceCount = await PurgeBatchedAsync(
                dbContext.DecisionTraces.IgnoreQueryFilters().Where(entity => !entity.IsDeleted && entity.CreatedAtUtc < decisionCutoffUtc),
                entity => entity.CreatedAtUtc,
                cancellationToken);

            var executionTraceCount = await PurgeBatchedAsync(
                dbContext.ExecutionTraces.IgnoreQueryFilters().Where(entity => !entity.IsDeleted && entity.CreatedAtUtc < executionCutoffUtc),
                entity => entity.CreatedAtUtc,
                cancellationToken);

            var adminAuditLogCount = dbContext.Database.IsRelational()
                ? await PurgeBatchedAsync(
                    dbContext.AdminAuditLogs.Where(entity => entity.CreatedAtUtc < adminAuditCutoffUtc),
                    entity => entity.CreatedAtUtc,
                    cancellationToken)
                : 0;

            var terminalIncidentIds = await dbContext.Incidents
                .AsNoTracking()
                .IgnoreQueryFilters()
                .Where(entity =>
                    !entity.IsDeleted &&
                    entity.CreatedDate < incidentCutoffUtc &&
                    (entity.Status == IncidentStatus.Resolved ||
                     entity.Status == IncidentStatus.Rejected ||
                     entity.Status == IncidentStatus.Expired ||
                     entity.Status == IncidentStatus.Cancelled ||
                     entity.Status == IncidentStatus.Failed))
                .Select(entity => entity.Id)
                .ToListAsync(cancellationToken);

            var incidentEventCount = terminalIncidentIds.Count == 0
                ? 0
                : await dbContext.IncidentEvents
                    .AsNoTracking()
                    .IgnoreQueryFilters()
                    .Where(entity => terminalIncidentIds.Contains(entity.IncidentId))
                    .CountAsync(cancellationToken);

            var incidentCount = await PurgeBatchedAsync(
                dbContext.Incidents
                    .IgnoreQueryFilters()
                    .Where(entity =>
                        !entity.IsDeleted &&
                        entity.CreatedDate < incidentCutoffUtc &&
                        (entity.Status == IncidentStatus.Resolved ||
                         entity.Status == IncidentStatus.Rejected ||
                         entity.Status == IncidentStatus.Expired ||
                         entity.Status == IncidentStatus.Cancelled ||
                         entity.Status == IncidentStatus.Failed)),
                entity => entity.CreatedDate,
                cancellationToken);

            var terminalApprovalIds = await dbContext.ApprovalQueues
                .AsNoTracking()
                .IgnoreQueryFilters()
                .Where(entity =>
                    !entity.IsDeleted &&
                    entity.CreatedDate < approvalCutoffUtc &&
                    (entity.Status == ApprovalQueueStatus.Approved ||
                     entity.Status == ApprovalQueueStatus.Executed ||
                     entity.Status == ApprovalQueueStatus.Rejected ||
                     entity.Status == ApprovalQueueStatus.Expired ||
                     entity.Status == ApprovalQueueStatus.Cancelled ||
                     entity.Status == ApprovalQueueStatus.Failed))
                .Select(entity => entity.Id)
                .ToListAsync(cancellationToken);

            var approvalActionCount = terminalApprovalIds.Count == 0
                ? 0
                : await dbContext.ApprovalActions
                    .AsNoTracking()
                    .IgnoreQueryFilters()
                    .Where(entity => terminalApprovalIds.Contains(entity.ApprovalQueueId))
                    .CountAsync(cancellationToken);

            var approvalQueueCount = await PurgeBatchedAsync(
                dbContext.ApprovalQueues
                    .IgnoreQueryFilters()
                    .Where(entity =>
                        !entity.IsDeleted &&
                        entity.CreatedDate < approvalCutoffUtc &&
                        (entity.Status == ApprovalQueueStatus.Approved ||
                         entity.Status == ApprovalQueueStatus.Executed ||
                         entity.Status == ApprovalQueueStatus.Rejected ||
                         entity.Status == ApprovalQueueStatus.Expired ||
                         entity.Status == ApprovalQueueStatus.Cancelled ||
                         entity.Status == ApprovalQueueStatus.Failed)),
                entity => entity.CreatedDate,
                cancellationToken);

            var completedAtUtc = timeProvider.GetUtcNow().UtcDateTime;

            await adminAuditLogService.WriteAsync(
                new AdminAuditLogWriteRequest(
                    "system:log-retention",
                    RetentionCompletedAction,
                    "LogCenterRetention",
                    targetSummary,
                    BuildRetentionPolicySummary(),
                    BuildRetentionCountsSummary(
                        decisionTraceCount,
                        executionTraceCount,
                        adminAuditLogCount,
                        incidentCount,
                        incidentEventCount,
                        approvalQueueCount,
                        approvalActionCount),
                    "Retention run completed.",
                    null,
                    null,
                    runCorrelationId),
                cancellationToken);

            return new LogCenterRetentionRunSnapshot(
                startedAtUtc,
                completedAtUtc,
                decisionTraceCount,
                executionTraceCount,
                adminAuditLogCount,
                incidentCount,
                incidentEventCount,
                approvalQueueCount,
                approvalActionCount);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            dbContext.ChangeTracker.Clear();

            try
            {
                await adminAuditLogService.WriteAsync(
                    new AdminAuditLogWriteRequest(
                        "system:log-retention",
                        RetentionFailedAction,
                        "LogCenterRetention",
                        "DecisionTrace|ExecutionTrace|AdminAuditLog|Incident|ApprovalQueue",
                        BuildRetentionPolicySummary(),
                        null,
                        Truncate($"Retention run failed: {exception.Message}", 512),
                        null,
                        null,
                        runCorrelationId),
                    cancellationToken);
            }
            catch (Exception auditException) when (auditException is not OperationCanceledException)
            {
                logger.LogWarning(auditException, "Failed to write log retention failure audit.");
            }

            logger.LogWarning(exception, "Log center retention run failed.");
            throw;
        }
    }

    private async Task<LogCenterRetentionSnapshot> BuildSnapshotAsync(CancellationToken cancellationToken)
    {
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

    private async Task<int> PurgeBatchedAsync<TEntity>(
        IQueryable<TEntity> query,
        Expression<Func<TEntity, DateTime>> orderSelector,
        CancellationToken cancellationToken)
        where TEntity : class
    {
        var totalDeleted = 0;
        var batchSize = Math.Clamp(optionsValue.BatchSize, 1, 1000);

        while (true)
        {
            var batchQuery = query.OrderBy(orderSelector).Take(batchSize);

            if (dbContext.Database.IsRelational())
            {
                var ids = await batchQuery
                    .Select(entity => EF.Property<Guid>(entity, "Id"))
                    .ToListAsync(cancellationToken);

                if (ids.Count == 0)
                {
                    break;
                }

                totalDeleted += await query
                    .Where(entity => ids.Contains(EF.Property<Guid>(entity, "Id")))
                    .ExecuteDeleteAsync(cancellationToken);
            }
            else
            {
                var batch = await batchQuery.ToListAsync(cancellationToken);

                if (batch.Count == 0)
                {
                    break;
                }

                dbContext.RemoveRange(batch);
                totalDeleted += batch.Count;
                await dbContext.SaveChangesAsync(cancellationToken);
                dbContext.ChangeTracker.Clear();
            }
        }

        return totalDeleted;
    }

    private static bool IsTerminal(IncidentStatus status)
    {
        return status is IncidentStatus.Resolved or IncidentStatus.Rejected or IncidentStatus.Expired or IncidentStatus.Cancelled or IncidentStatus.Failed;
    }

    private static bool IsTerminal(ApprovalQueueStatus status)
    {
        return status is ApprovalQueueStatus.Approved or ApprovalQueueStatus.Executed or ApprovalQueueStatus.Rejected or ApprovalQueueStatus.Expired or ApprovalQueueStatus.Cancelled or ApprovalQueueStatus.Failed;
    }

    private string BuildRetentionPolicySummary()
    {
        return
            $"Enabled={optionsValue.Enabled}; DecisionTrace={optionsValue.DecisionTraceRetentionDays}d; ExecutionTrace={optionsValue.ExecutionTraceRetentionDays}d; AdminAuditLog={optionsValue.AdminAuditLogRetentionDays}d; Incident={optionsValue.IncidentRetentionDays}d; Approval={optionsValue.ApprovalRetentionDays}d; BatchSize={optionsValue.BatchSize}";
    }

    private static string BuildRetentionCountsSummary(
        int decisionTraceCount,
        int executionTraceCount,
        int adminAuditLogCount,
        int incidentCount,
        int incidentEventCount,
        int approvalQueueCount,
        int approvalActionCount)
    {
        return
            $"DecisionTrace={decisionTraceCount}; ExecutionTrace={executionTraceCount}; AdminAuditLog={adminAuditLogCount}; Incident={incidentCount}; IncidentEvent={incidentEventCount}; ApprovalQueue={approvalQueueCount}; ApprovalAction={approvalActionCount}";
    }

    private static string Truncate(string? value, int maxLength)
    {
        var normalized = value?.Trim() ?? string.Empty;
        return normalized.Length <= maxLength
            ? normalized
            : normalized[..maxLength];
    }

    private LogCenterRetentionSnapshot CreateFallbackSnapshot(DateTime? lastRunAtUtc, string? lastRunSummary)
    {
        return new LogCenterRetentionSnapshot(
            optionsValue.Enabled,
            optionsValue.DecisionTraceRetentionDays,
            optionsValue.ExecutionTraceRetentionDays,
            optionsValue.AdminAuditLogRetentionDays,
            optionsValue.IncidentRetentionDays,
            optionsValue.ApprovalRetentionDays,
            optionsValue.BatchSize,
            lastRunAtUtc,
            lastRunSummary);
    }
}
