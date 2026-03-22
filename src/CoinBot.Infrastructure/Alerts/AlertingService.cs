using CoinBot.Application.Abstractions.Alerts;
using Microsoft.Extensions.Logging;

namespace CoinBot.Infrastructure.Alerts;

public sealed class AlertingService(
    IEnumerable<IAlertProvider> providers,
    ILogger<AlertingService> logger) : IAlertService
{
    public async Task SendAsync(AlertNotification notification, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);

        var enabledProviders = providers
            .Where(provider => provider.IsEnabled)
            .ToArray();

        if (enabledProviders.Length == 0)
        {
            logger.LogDebug("Alert code {AlertCode} was skipped because no alert provider is enabled.", notification.Code);
            return;
        }

        foreach (var provider in enabledProviders)
        {
            try
            {
                await provider.SendAsync(notification, cancellationToken);
                logger.LogInformation(
                    "Alert code {AlertCode} was dispatched via provider {AlertProvider}.",
                    notification.Code,
                    provider.Name);
            }
            catch
            {
                logger.LogWarning(
                    "Alert provider {AlertProvider} failed while dispatching alert code {AlertCode}.",
                    provider.Name,
                    notification.Code);
            }
        }
    }
}