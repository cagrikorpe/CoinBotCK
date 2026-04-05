using CoinBot.Application.Abstractions.Dashboard;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Execution;
using CoinBot.Infrastructure.Jobs;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CoinBot.Infrastructure.Dashboard;

public sealed class UserDashboardOperationsReadModelService(
    ApplicationDbContext dbContext,
    IOptions<BotExecutionPilotOptions> options,
    IOptions<DataLatencyGuardOptions> dataLatencyGuardOptions) : IUserDashboardOperationsReadModelService
{
    private sealed record EnabledBotSnapshot(Guid Id, string? Symbol);

    private readonly BotExecutionPilotOptions optionsValue = options.Value;
    private readonly DataLatencyGuardOptions dataLatencyGuardOptionsValue = dataLatencyGuardOptions.Value;

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
        var degradedModeState = await dbContext.DegradedModeStates
            .AsNoTracking()
            .IgnoreQueryFilters()
            .SingleOrDefaultAsync(
                entity => entity.Id == DegradedModeDefaults.SingletonId,
                cancellationToken);
        var clockDriftProbeSnapshot = await dbContext.HealthSnapshots
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(entity => entity.SnapshotKey == "clock-drift-monitor")
            .OrderByDescending(entity => entity.LastUpdatedAtUtc)
            .ThenByDescending(entity => entity.ObservedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
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
            .Where(entity =>
                entity.OwnerUserId == normalizedUserId &&
                entity.Plane == ExchangeDataPlane.Futures &&
                !entity.IsDeleted)
            .ToListAsync(cancellationToken);
        var positions = await dbContext.ExchangePositions
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(entity =>
                entity.OwnerUserId == normalizedUserId &&
                entity.Plane == ExchangeDataPlane.Futures &&
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
        var driftThresholdMilliseconds = checked(dataLatencyGuardOptionsValue.ClockDriftThresholdSeconds * 1000);
        var driftSummary = BuildDriftSummary(degradedModeState, clockDriftProbeSnapshot, driftThresholdMilliseconds);
        var driftReason = BuildDriftReason(degradedModeState, clockDriftProbeSnapshot, driftThresholdMilliseconds);

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
            LastExecutionAtUtc: latestExecution?.CreatedDate,
            DriftSummary: driftSummary,
            DriftReason: driftReason);
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

    private static string BuildDriftSummary(
        DegradedModeState? degradedModeState,
        HealthSnapshot? clockDriftProbeSnapshot,
        int driftThresholdMilliseconds)
    {
        var probeDriftMilliseconds = TryReadDetailInt(clockDriftProbeSnapshot?.Detail, "ClockDriftMs");
        var probeUpdatedAtUtc = clockDriftProbeSnapshot?.LastUpdatedAtUtc;
        var guardDriftMilliseconds = degradedModeState?.LatestClockDriftMilliseconds;
        var stateCode = degradedModeState?.StateCode.ToString() ?? "Unknown";

        if (degradedModeState?.ReasonCode == DegradedModeReasonCode.ClockDriftExceeded)
        {
            return
                $"Heartbeat drift {(guardDriftMilliseconds?.ToString() ?? "n/a")} / {driftThresholdMilliseconds} ms • Server probe {(probeDriftMilliseconds?.ToString() ?? "n/a")} ms • Last probe {FormatUtc(probeUpdatedAtUtc)}";
        }

        return
            $"Server probe {(probeDriftMilliseconds?.ToString() ?? "n/a")} / {driftThresholdMilliseconds} ms • Guard {stateCode} • Last probe {FormatUtc(probeUpdatedAtUtc)}";
    }

    private static string BuildDriftReason(
        DegradedModeState? degradedModeState,
        HealthSnapshot? clockDriftProbeSnapshot,
        int driftThresholdMilliseconds)
    {
        var probeDriftMilliseconds = TryReadDetailInt(clockDriftProbeSnapshot?.Detail, "ClockDriftMs");

        return degradedModeState?.ReasonCode switch
        {
            DegradedModeReasonCode.ClockDriftExceeded =>
                $"Execution block kaynağı market-data heartbeat. Latest heartbeat drift {(degradedModeState.LatestClockDriftMilliseconds?.ToString() ?? "n/a")} ms, threshold {driftThresholdMilliseconds} ms. Server-time refresh signed REST offset'ini yeniler; fresh kline heartbeat gelmeden blok sürebilir.",
            DegradedModeReasonCode.MarketDataLatencyBreached or DegradedModeReasonCode.MarketDataLatencyCritical =>
                $"Market-data freshness guard aktif. Latest data age {(degradedModeState.LatestHeartbeatReceivedAtUtc.HasValue && degradedModeState.LatestDataTimestampAtUtc.HasValue ? Math.Max(0, (int)Math.Round((degradedModeState.LatestHeartbeatReceivedAtUtc.Value - degradedModeState.LatestDataTimestampAtUtc.Value).TotalMilliseconds, MidpointRounding.AwayFromZero)).ToString() : "n/a")} ms, latest heartbeat drift {(degradedModeState.LatestClockDriftMilliseconds?.ToString() ?? "n/a")} ms.",
            DegradedModeReasonCode.MarketDataUnavailable =>
                "Market-data heartbeat henüz güvenli kabul edilecek kadar güncel değil. Server-time probe sağlıklı olsa bile execution açılmaz.",
            _ =>
                $"Signed REST server-time probe {(probeDriftMilliseconds?.ToString() ?? "n/a")} ms. Market-data guard state {(degradedModeState?.StateCode.ToString() ?? "Unknown")} / {(degradedModeState?.ReasonCode.ToString() ?? "Unknown")}."
        };
    }

    private static int? TryReadDetailInt(string? detail, string key)
    {
        if (string.IsNullOrWhiteSpace(detail))
        {
            return null;
        }

        var segments = detail.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var segment in segments)
        {
            var separatorIndex = segment.IndexOf('=');

            if (separatorIndex <= 0)
            {
                continue;
            }

            if (!string.Equals(segment[..separatorIndex].Trim(), key, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return int.TryParse(segment[(separatorIndex + 1)..].Trim(), out var value)
                ? value
                : null;
        }

        return null;
    }

    private static string FormatUtc(DateTime? utcTimestamp)
    {
        return utcTimestamp.HasValue
            ? utcTimestamp.Value.ToUniversalTime().ToString("HH:mm:ss") + " UTC"
            : "Henüz yok";
    }
}
