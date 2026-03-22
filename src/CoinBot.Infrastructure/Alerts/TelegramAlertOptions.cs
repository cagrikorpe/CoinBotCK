namespace CoinBot.Infrastructure.Alerts;

public sealed class TelegramAlertOptions
{
    public bool Enabled { get; set; }

    public string BotToken { get; set; } = string.Empty;

    public string ChatId { get; set; } = string.Empty;
}