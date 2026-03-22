using System.ComponentModel.DataAnnotations;

namespace CoinBot.Infrastructure.MarketData;

public sealed class BinanceMarketDataOptions
{
    public bool Enabled { get; set; }

    [Required]
    public string RestBaseUrl { get; set; } = "https://api.binance.com";

    [Required]
    public string WebSocketBaseUrl { get; set; } = "wss://stream.binance.com:9443";

    [Required]
    public string KlineInterval { get; set; } = "1m";

    [Range(1, 1440)]
    public int ExchangeInfoRefreshIntervalMinutes { get; set; } = 60;

    [Range(1, 300)]
    public int ReconnectDelaySeconds { get; set; } = 5;

    [Range(1, 60)]
    public int HeartbeatPersistenceIntervalSeconds { get; set; } = 1;

    public string[] SeedSymbols { get; set; } = [];
}
