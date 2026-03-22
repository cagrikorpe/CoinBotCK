using System.Net.Http.Json;
using CoinBot.Application.Abstractions.Alerts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CoinBot.Infrastructure.Alerts;

public sealed class SlackAlertProvider(
    HttpClient httpClient,
    IOptions<AlertingOptions> alertingOptions,
    IOptions<SlackAlertOptions> options,
    ILogger<SlackAlertProvider> logger) : IAlertProvider
{
    private readonly AlertingOptions alertingOptionsValue = alertingOptions.Value;
    private readonly SlackAlertOptions optionsValue = options.Value;

    public string Name => "Slack";

    public bool IsEnabled => optionsValue.Enabled && Uri.IsWellFormedUriString(optionsValue.WebhookUrl, UriKind.Absolute);

    public async Task SendAsync(AlertNotification notification, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);

        if (!IsEnabled)
        {
            return;
        }

        using var response = await httpClient.PostAsJsonAsync(
            optionsValue.WebhookUrl,
            new { text = BuildMessage(notification) },
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException("Slack alert dispatch failed.");
        }

        logger.LogDebug("Slack provider accepted alert code {AlertCode}.", notification.Code);
    }

    private string BuildMessage(AlertNotification notification)
    {
        var applicationName = string.IsNullOrWhiteSpace(alertingOptionsValue.ApplicationName)
            ? "CoinBot"
            : alertingOptionsValue.ApplicationName.Trim();

        return $"{applicationName} [{notification.Severity}] {notification.Title}\nCode: {notification.Code}\n{notification.Message}";
    }
}