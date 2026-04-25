using CoinBot.Application.Abstractions.Administration;

namespace CoinBot.Application.Abstractions.Monitoring;

public sealed record MonitoringDashboardSnapshot(
    IReadOnlyCollection<HealthSnapshot> HealthSnapshots,
    IReadOnlyCollection<WorkerHeartbeat> WorkerHeartbeats,
    DateTime LastRefreshedAtUtc)
{
    public MarketScannerDashboardSnapshot MarketScanner { get; init; } = MarketScannerDashboardSnapshot.Empty();

    public SharedMarketDataCacheHealthSnapshot MarketDataCache { get; init; } = SharedMarketDataCacheHealthSnapshot.Empty();

    public UltraDebugLogHealthSnapshot UltraDebugLogHealth { get; init; } = UltraDebugLogHealthSnapshot.Empty();

    public static MonitoringDashboardSnapshot Empty(DateTime lastRefreshedAtUtc)
    {
        return new MonitoringDashboardSnapshot(Array.Empty<HealthSnapshot>(), Array.Empty<WorkerHeartbeat>(), lastRefreshedAtUtc)
        {
            MarketScanner = MarketScannerDashboardSnapshot.Empty(),
            MarketDataCache = SharedMarketDataCacheHealthSnapshot.Empty(lastRefreshedAtUtc),
            UltraDebugLogHealth = UltraDebugLogHealthSnapshot.Empty()
        };
    }
}
