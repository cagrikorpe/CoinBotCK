namespace CoinBot.Application.Abstractions.Monitoring;

public sealed record MonitoringDashboardSnapshot(
    IReadOnlyCollection<HealthSnapshot> HealthSnapshots,
    IReadOnlyCollection<WorkerHeartbeat> WorkerHeartbeats,
    DateTime LastRefreshedAtUtc)
{
    public static MonitoringDashboardSnapshot Empty(DateTime lastRefreshedAtUtc)
    {
        return new MonitoringDashboardSnapshot(Array.Empty<HealthSnapshot>(), Array.Empty<WorkerHeartbeat>(), lastRefreshedAtUtc);
    }
}
