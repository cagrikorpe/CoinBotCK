namespace CoinBot.Infrastructure.Alerts;

public sealed class SlackAlertOptions
{
    public bool Enabled { get; set; }

    public string WebhookUrl { get; set; } = string.Empty;
}