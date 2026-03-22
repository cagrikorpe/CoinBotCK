using CoinBot.Application.Abstractions.Auditing;
using CoinBot.Application.Abstractions.Execution;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CoinBot.Infrastructure.Execution;

public sealed class GlobalExecutionSwitchService(
    ApplicationDbContext dbContext,
    IAuditLogService auditLogService) : IGlobalExecutionSwitchService
{
    public async Task<GlobalExecutionSwitchSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var switchEntity = await dbContext.GlobalExecutionSwitches
            .AsNoTracking()
            .SingleOrDefaultAsync(entity => entity.Id == GlobalExecutionSwitchDefaults.SingletonId, cancellationToken);

        return switchEntity is null || switchEntity.IsDeleted
            ? CreateUnconfiguredSnapshot()
            : MapSnapshot(switchEntity, isPersisted: true);
    }

    public async Task<GlobalExecutionSwitchSnapshot> SetTradeMasterStateAsync(
        TradeMasterSwitchState tradeMasterState,
        string actor,
        string? context = null,
        string? correlationId = null,
        CancellationToken cancellationToken = default)
    {
        var switchEntity = await GetOrCreateTrackedEntityAsync(cancellationToken);
        var hasChanged = switchEntity.TradeMasterState != tradeMasterState;

        switchEntity.TradeMasterState = tradeMasterState;

        var snapshot = MapSnapshot(switchEntity, isPersisted: true);

        await auditLogService.WriteAsync(
            new AuditLogWriteRequest(
                actor,
                tradeMasterState == TradeMasterSwitchState.Armed ? "TradeMaster.Armed" : "TradeMaster.Disarmed",
                "GlobalExecutionSwitch/TradeMaster",
                context,
                correlationId,
                hasChanged ? "Applied" : "NoChange",
                snapshot.EffectiveEnvironment.ToString()),
            cancellationToken);

        return snapshot;
    }

    public async Task<GlobalExecutionSwitchSnapshot> SetDemoModeAsync(
        bool isEnabled,
        string actor,
        TradingModeLiveApproval? liveApproval = null,
        string? context = null,
        string? correlationId = null,
        CancellationToken cancellationToken = default)
    {
        var switchEntity = await GetOrCreateTrackedEntityAsync(cancellationToken);
        var hasChanged = switchEntity.DemoModeEnabled != isEnabled;
        var targetMode = isEnabled ? ExecutionEnvironment.Demo : ExecutionEnvironment.Live;

        if (!isEnabled &&
            (switchEntity.DemoModeEnabled || switchEntity.LiveModeApprovedAtUtc is null) &&
            !HasApproval(liveApproval))
        {
            await auditLogService.WriteAsync(
                new AuditLogWriteRequest(
                    actor,
                    "DemoMode.Disabled",
                    "GlobalExecutionSwitch/DemoMode",
                    context,
                    correlationId,
                    "Blocked:LiveApprovalRequired",
                    targetMode.ToString()),
                cancellationToken);

            throw new InvalidOperationException("Explicit live approval is required before the global default mode can switch to Live.");
        }

        if (hasChanged && await HasInheritedOpenExposureAsync(cancellationToken))
        {
            await auditLogService.WriteAsync(
                new AuditLogWriteRequest(
                    actor,
                    isEnabled ? "DemoMode.Enabled" : "DemoMode.Disabled",
                    "GlobalExecutionSwitch/DemoMode",
                    context,
                    correlationId,
                    "Blocked:OpenExposurePresent",
                    targetMode.ToString()),
                cancellationToken);

            throw new InvalidOperationException("Global default trading mode cannot change while inheriting bots have open orders or positions.");
        }

        switchEntity.DemoModeEnabled = isEnabled;

        if (isEnabled)
        {
            switchEntity.LiveModeApprovedAtUtc = null;
            switchEntity.LiveModeApprovalReference = null;
        }
        else if (liveApproval is not null)
        {
            switchEntity.LiveModeApprovedAtUtc = liveApproval.ApprovedAtUtc?.ToUniversalTime() ?? DateTime.UtcNow;
            switchEntity.LiveModeApprovalReference = NormalizeApprovalReference(liveApproval.ApprovalReference);
        }

        var snapshot = MapSnapshot(switchEntity, isPersisted: true);

        await auditLogService.WriteAsync(
            new AuditLogWriteRequest(
                actor,
                isEnabled ? "DemoMode.Enabled" : "DemoMode.Disabled",
                "GlobalExecutionSwitch/DemoMode",
                context,
                correlationId,
                hasChanged ? "Applied" : "NoChange",
                snapshot.EffectiveEnvironment.ToString()),
            cancellationToken);

        return snapshot;
    }

    private async Task<GlobalExecutionSwitch> GetOrCreateTrackedEntityAsync(CancellationToken cancellationToken)
    {
        var switchEntity = await dbContext.GlobalExecutionSwitches
            .SingleOrDefaultAsync(entity => entity.Id == GlobalExecutionSwitchDefaults.SingletonId, cancellationToken);

        if (switchEntity is null)
        {
            switchEntity = GlobalExecutionSwitchDefaults.CreateEntity();
            dbContext.GlobalExecutionSwitches.Add(switchEntity);
            return switchEntity;
        }

        if (switchEntity.IsDeleted)
        {
            switchEntity.IsDeleted = false;
        }

        return switchEntity;
    }

    private static GlobalExecutionSwitchSnapshot CreateUnconfiguredSnapshot()
    {
        return new GlobalExecutionSwitchSnapshot(
            TradeMasterSwitchState.Disarmed,
            DemoModeEnabled: true,
            IsPersisted: false);
    }

    private static GlobalExecutionSwitchSnapshot MapSnapshot(GlobalExecutionSwitch switchEntity, bool isPersisted)
    {
        return new GlobalExecutionSwitchSnapshot(
            switchEntity.TradeMasterState,
            switchEntity.DemoModeEnabled,
            isPersisted,
            switchEntity.LiveModeApprovedAtUtc);
    }

    private async Task<bool> HasInheritedOpenExposureAsync(CancellationToken cancellationToken)
    {
        var userOverrides = await dbContext.Users
            .AsNoTracking()
            .Where(entity => entity.TradingModeOverride != null)
            .Select(entity => entity.Id)
            .ToHashSetAsync(cancellationToken);

        return await dbContext.TradingBots
            .IgnoreQueryFilters()
            .AnyAsync(
                entity => !entity.IsDeleted &&
                          entity.TradingModeOverride == null &&
                          !userOverrides.Contains(entity.OwnerUserId) &&
                          (entity.OpenOrderCount > 0 || entity.OpenPositionCount > 0),
                cancellationToken);
    }

    private static bool HasApproval(TradingModeLiveApproval? liveApproval)
    {
        return !string.IsNullOrWhiteSpace(liveApproval?.ApprovalReference);
    }

    private static string NormalizeApprovalReference(string? approvalReference)
    {
        var normalizedReference = approvalReference?.Trim();

        if (string.IsNullOrWhiteSpace(normalizedReference))
        {
            throw new InvalidOperationException("Approval reference is required when switching the global default mode to Live.");
        }

        if (normalizedReference.Length > 128)
        {
            throw new ArgumentOutOfRangeException(nameof(approvalReference), "Approval reference cannot exceed 128 characters.");
        }

        return normalizedReference;
    }
}
