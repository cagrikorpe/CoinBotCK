namespace CoinBot.Application.Abstractions.Alerts;

public interface IAlertService
{
    Task SendAsync(AlertNotification notification, CancellationToken cancellationToken = default);
}
