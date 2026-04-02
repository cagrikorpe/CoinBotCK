namespace CoinBot.Application.Abstractions.Dashboard;

public interface IUserDashboardOperationsReadModelService
{
    Task<UserDashboardOperationsSummarySnapshot> GetSnapshotAsync(
        string userId,
        CancellationToken cancellationToken = default);
}

public sealed record UserDashboardOperationsSummarySnapshot(
    int EnabledBotCount,
    int EnabledSymbolCount,
    int ConflictedSymbolCount,
    string LastJobStatus,
    string? LastJobErrorCode,
    string LastExecutionState,
    string? LastExecutionFailureCode,
    string WorkerHealthLabel,
    string WorkerHealthTone,
    string PrivateStreamHealthLabel,
    string PrivateStreamHealthTone,
    string BreakerLabel,
    string BreakerTone,
    int OpenCircuitBreakerCount,
    decimal? CurrentDailyLossPercentage,
    decimal? MaxDailyLossPercentage,
    int OpenPositionCount,
    int MaxOpenPositions,
    int ActiveBotCooldownCount,
    int ActiveSymbolCooldownCount,
    DateTime? LastExecutionAtUtc);
