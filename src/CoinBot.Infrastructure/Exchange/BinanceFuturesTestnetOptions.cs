namespace CoinBot.Infrastructure.Exchange;

public sealed class BinanceFuturesTestnetOptions
{
    public string? BaseUrl { get; set; }

    public string? ApiKey { get; set; }

    public string? ApiSecret { get; set; }

    public bool AllowConfiguredCredentialFallback { get; set; }
}
