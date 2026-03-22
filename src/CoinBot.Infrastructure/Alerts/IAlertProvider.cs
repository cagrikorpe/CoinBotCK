using CoinBot.Application.Abstractions.Alerts;

namespace CoinBot.Infrastructure.Alerts;

public interface IAlertProvider
{
    string Name { get; }

    bool IsEnabled { get; }

    Task SendAsync(AlertNotification notification, CancellationToken cancellationToken = default);
}