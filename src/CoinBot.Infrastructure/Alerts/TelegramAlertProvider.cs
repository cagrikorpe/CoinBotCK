using CoinBot.Application.Abstractions.Alerts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CoinBot.Infrastructure.Alerts;

public sealed class TelegramAlertProvider(
    HttpClient httpClient,
    IOptions<AlertingOptions> alertingOptions,
    IOptions<TelegramAlertOptions> options,
    ILogger<TelegramAlertProvider> logger) : IAlertProvider
{
    private readonly AlertingOptions alertingOptionsValue = alertingOptions.Value;
    private readonly TelegramAlertOptions optionsValue = options.Value;

    public string Name => "Telegram";

    public bool IsEnabled => optionsValue.Enabled &&
                             !string.IsNullOrWhiteSpace(optionsValue.BotToken) &&
                             !string.IsNullOrWhiteSpace(optionsValue.ChatId);

    public async Task SendAsync(AlertNotification notification, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);

        if (!IsEnabled)
        {
            return;
        }

        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["chat_id"] = optionsValue.ChatId.Trim(),
            ["text"] = BuildMessage(notification)
        });

        using var response = await httpClient.PostAsync(BuildEndpoint(), content, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException("Telegram alert dispatch failed.");
        }

        logger.LogDebug("Telegram provider accepted alert code {AlertCode}.", notification.Code);
    }

    private string BuildEndpoint()
    {
        return $"https://api.telegram.org/bot{optionsValue.BotToken.Trim()}/sendMessage";
    }

    private string BuildMessage(AlertNotification notification)
    {
        var applicationName = string.IsNullOrWhiteSpace(alertingOptionsValue.ApplicationName)
            ? "CoinBot"
            : alertingOptionsValue.ApplicationName.Trim();

        return $"{applicationName} [{notification.Severity}] {notification.Title}\nCode: {notification.Code}\n{notification.Message}";
    }
}