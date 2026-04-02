using CoinBot.Application.Abstractions.Dashboard;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Jobs;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CoinBot.Infrastructure.Dashboard;

public sealed class UserDashboardOperationsReadModelService(
    ApplicationDbContext dbContext,
    IOptions<BotExecutionPilotOptions> options) : IUserDashboardOperationsReadModelService
{
    private sealed record EnabledBotSnapshot(Guid Id, string? Symbol);

    private readonly BotExecutionPilotOptions optionsValue = options.Value;

    public async Task<UserDashboardOperationsSummarySnapshot> GetSnapshotAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        var normalizedUserId = NormalizeRequired(userId, nameof(userId));
        var enabledBots = await dbContext.TradingBots
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(entity =>
                entity.OwnerUserId == normalizedUserId &&
                entity.IsEnabled &&
                !entity.IsDeleted)
            .Select(entity => new EnabledBotSnapshot(entity.Id, entity.Symbol))
            .ToListAsync(cancellationToken);

        var normalizedSymbols = enabledBots
            .Select(entity => NormalizeSymbol(entity.Symbol))
            .ToArray();
        var botIds = enabledBots.Select(entity => entity.Id).ToArray();
        var latestJobState = await dbContext.BackgroundJobStates
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(entity =>
                entity.BotId.HasValue &&
                botIds.Contains(entity.BotId.Value) &&
                !entity.IsDeleted)
            .OrderByDescending(entity => entity.LastHeartbeatAtUtc ?? entity.LastFailedAtUtc ?? entity.LastCompletedAtUtc ?? entity.NextRunAtUtc)
            .ThenByDescending(entity => entity.UpdatedDate)
            .FirstOrDefaultAsync(cancellationToken);
        var latestExecution = await dbContext.ExecutionOrders
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(entity =>
                entity.OwnerUserId == normalizedUserId &&
                !entity.IsDeleted)
            .OrderByDescending(entity => entity.CreatedDate)
            .FirstOrDefaultAsync(cancellationToken);
        var workerHeartbeat = await dbContext.WorkerHeartbeats
            .AsNoTracking()
            .IgnoreQueryFilters()
            .SingleOrDefaultAsync(entity => entity.WorkerKey == "job-orchestration", cancellationToken);
        var privateStreamHeartbeat = await dbContext.WorkerHeartbeats
            .AsNoTracking()
            .IgnoreQueryFilters()
            .SingleOrDefaultAsync(entity => entity.WorkerKey == "exchange-private-stream", cancellationToken);
        var breakerStates = await dbContext.DependencyCircuitBreakerStates
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(entity => !entity.IsDeleted && entity.StateCode != CircuitBreakerStateCode.Closed)
            .ToListAsync(cancellationToken);
        var riskProfile = await dbContext.RiskProfiles
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(entity => entity.OwnerUserId == normalizedUserId && !entity.IsDeleted)
            .OrderByDescending(entity => entity.UpdatedDate)
            .ThenByDescending(entity => entity.CreatedDate)
            .FirstOrDefaultAsync(cancellationToken);
        var balances = await dbContext.ExchangeBalances
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(entity => entity.OwnerUserId == normalizedUserId && !entity.IsDeleted)
            .ToListAsync(cancellationToken);
        var positions = await dbContext.ExchangePositions
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(entity =>
                entity.OwnerUserId == normalizedUserId &&
                !entity.IsDeleted &&
                entity.Quantity != 0m)
            .ToListAsync(cancellationToken);

        var equity = balances.Sum(entity =>
            entity.CrossWalletBalance != 0m
                ? entity.CrossWalletBalance
                : entity.WalletBalance);
        var currentDailyLossAmount = positions
            .Where(entity => entity.UnrealizedProfit < 0m)
            .Sum(entity => Math.Abs(entity.UnrealizedProfit));
        var currentDailyLossPercentage = equity > 0m
            ? (decimal?)((currentDailyLossAmount / equity) * 100m)
            : null;
        var activeBotCooldownCount = await ResolveActiveBotCooldownCountAsync(normalizedUserId, enabledBots, cancellationToken);
        var activeSymbolCooldownCount = await ResolveActiveSymbolCooldownCountAsync(normalizedUserId, normalizedSymbols, cancellationToken);
        var conflictedSymbolCount = normalizedSymbols
            .GroupBy(symbol => symbol, StringComparer.Ordinal)
            .Count(group => group.Count() > 1);

        return new UserDashboardOperationsSummarySnapshot(
            EnabledBotCount: enabledBots.Count,
            EnabledSymbolCount: normalizedSymbols.Distinct(StringComparer.Ordinal).Count(),
            ConflictedSymbolCount: conflictedSymbolCount,
            LastJobStatus: latestJobState?.Status.ToString() ?? "Idle",
            LastJobErrorCode: latestJobState?.LastErrorCode,
            LastExecutionState: latestExecution?.State.ToString() ?? "N/A",
            LastExecutionFailureCode: latestExecution?.FailureCode,
            WorkerHealthLabel: workerHeartbeat?.HealthState.ToString() ?? "Unknown",
            WorkerHealthTone: MapHealthTone(workerHeartbeat?.HealthState),
            PrivateStreamHealthLabel: privateStreamHeartbeat?.HealthState.ToString() ?? "Unknown",
            PrivateStreamHealthTone: MapHealthTone(privateStreamHeartbeat?.HealthState),
            BreakerLabel: breakerStates.Count == 0 ? "Closed" : $"{breakerStates.Count} active",
            BreakerTone: breakerStates.Count == 0 ? "positive" : "warning",
            OpenCircuitBreakerCount: breakerStates.Count,
            CurrentDailyLossPercentage: currentDailyLossPercentage,
            MaxDailyLossPercentage: riskProfile?.MaxDailyLossPercentage,
            OpenPositionCount: positions.Count,
            MaxOpenPositions: optionsValue.MaxOpenPositionsPerUser,
            ActiveBotCooldownCount: activeBotCooldownCount,
            ActiveSymbolCooldownCount: activeSymbolCooldownCount,
            LastExecutionAtUtc: latestExecution?.CreatedDate);
    }

    private async Task<int> ResolveActiveBotCooldownCountAsync(
        string userId,
        IReadOnlyCollection<EnabledBotSnapshot> enabledBots,
        CancellationToken cancellationToken)
    {
        if (optionsValue.PerBotCooldownSeconds <= 0 || enabledBots.Count == 0)
        {
            return 0;
        }

        var thresholdUtc = DateTime.UtcNow.AddSeconds(-optionsValue.PerBotCooldownSeconds);
        var botIds = enabledBots.Select(entity => (Guid)entity.Id).ToArray();

        return await dbContext.ExecutionOrders
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(entity =>
                entity.OwnerUserId == userId &&
                entity.BotId.HasValue &&
                botIds.Contains(entity.BotId.Value) &&
                entity.CreatedDate >= thresholdUtc &&
                !entity.IsDeleted)
            .Select(entity => entity.BotId!.Value)
            .Distinct()
            .CountAsync(cancellationToken);
    }

    private async Task<int> ResolveActiveSymbolCooldownCountAsync(
        string userId,
        IReadOnlyCollection<string> normalizedSymbols,
        CancellationToken cancellationToken)
    {
        if (optionsValue.PerSymbolCooldownSeconds <= 0 || normalizedSymbols.Count == 0)
        {
            return 0;
        }

        var thresholdUtc = DateTime.UtcNow.AddSeconds(-optionsValue.PerSymbolCooldownSeconds);

        return await dbContext.ExecutionOrders
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(entity =>
                entity.OwnerUserId == userId &&
                normalizedSymbols.Contains(entity.Symbol) &&
                entity.CreatedDate >= thresholdUtc &&
                !entity.IsDeleted)
            .Select(entity => entity.Symbol)
            .Distinct()
            .CountAsync(cancellationToken);
    }

    private string NormalizeSymbol(string? symbol)
    {
        return string.IsNullOrWhiteSpace(symbol)
            ? optionsValue.DefaultSymbol.Trim().ToUpperInvariant()
            : symbol.Trim().ToUpperInvariant();
    }

    private static string MapHealthTone(MonitoringHealthState? healthState)
    {
        return healthState switch
        {
            MonitoringHealthState.Healthy => "positive",
            MonitoringHealthState.Warning or MonitoringHealthState.Degraded => "warning",
            MonitoringHealthState.Critical => "negative",
            _ => "neutral"
        };
    }

    private static string NormalizeRequired(string? value, string parameterName)
    {
        var normalizedValue = value?.Trim();

        if (string.IsNullOrWhiteSpace(normalizedValue))
        {
            throw new ArgumentException("The value is required.", parameterName);
        }

        return normalizedValue;
    }
}
