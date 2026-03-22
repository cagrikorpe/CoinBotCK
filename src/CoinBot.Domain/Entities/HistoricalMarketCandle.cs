namespace CoinBot.Domain.Entities;

public sealed class HistoricalMarketCandle : BaseEntity
{
    public string Symbol { get; set; } = string.Empty;

    public string Interval { get; set; } = string.Empty;

    public DateTime OpenTimeUtc { get; set; }

    public DateTime CloseTimeUtc { get; set; }

    public decimal OpenPrice { get; set; }

    public decimal HighPrice { get; set; }

    public decimal LowPrice { get; set; }

    public decimal ClosePrice { get; set; }

    public decimal Volume { get; set; }

    public DateTime ReceivedAtUtc { get; set; }

    public string Source { get; set; } = string.Empty;
}
