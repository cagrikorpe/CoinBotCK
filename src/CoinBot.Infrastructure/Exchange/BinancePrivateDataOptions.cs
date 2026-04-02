using System.ComponentModel.DataAnnotations;

namespace CoinBot.Infrastructure.Exchange;

public sealed class BinancePrivateDataOptions
{
    public bool Enabled { get; set; }

    [Required]
    public string RestBaseUrl { get; set; } = "https://fapi.binance.com";

    [Required]
    public string WebSocketBaseUrl { get; set; } = "wss://fstream.binance.com";

    [Range(5, 300)]
    public int SessionScanIntervalSeconds { get; set; } = 15;

    [Range(1, 300)]
    public int ReconnectDelaySeconds { get; set; } = 5;

    [Range(1, 59)]
    public int ListenKeyRenewalIntervalMinutes { get; set; } = 30;

    [Range(1, 60)]
    public int ReconciliationIntervalMinutes { get; set; } = 5;

    [Range(1000, 60000)]
    public int RecvWindowMilliseconds { get; set; } = 5000;

    [Range(5, 300)]
    public int ServerTimeSyncRefreshSeconds { get; set; } = 30;
}
