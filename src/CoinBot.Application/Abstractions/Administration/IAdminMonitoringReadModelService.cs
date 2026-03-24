using CoinBot.Application.Abstractions.Monitoring;

namespace CoinBot.Application.Abstractions.Administration;

public interface IAdminMonitoringReadModelService
{
    Task<MonitoringDashboardSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);
}
