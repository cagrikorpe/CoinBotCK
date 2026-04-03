namespace CoinBot.Domain.Entities;

public sealed class MarketScannerCandidate : BaseEntity
{
    public Guid ScanCycleId { get; set; }

    public string Symbol { get; set; } = string.Empty;

    public string UniverseSource { get; set; } = string.Empty;

    public DateTime ObservedAtUtc { get; set; }

    public DateTime? LastCandleAtUtc { get; set; }

    public decimal? LastPrice { get; set; }

    public decimal? QuoteVolume24h { get; set; }

    public decimal MarketScore { get; set; }

    public int? StrategyScore { get; set; }

    public string? ScoringSummary { get; set; }

    public bool IsEligible { get; set; }

    public string? RejectionReason { get; set; }

    public decimal Score { get; set; }

    public int? Rank { get; set; }

    public bool IsTopCandidate { get; set; }
}
