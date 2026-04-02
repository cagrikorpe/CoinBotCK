using CoinBot.Application.Abstractions.Alerts;

namespace CoinBot.Infrastructure.Alerts;

public interface IAlertDispatchCoordinator
{
    Task SendAsync(
        AlertNotification notification,
        string dedupeKey,
        TimeSpan cooldown,
        CancellationToken cancellationToken = default);
}
