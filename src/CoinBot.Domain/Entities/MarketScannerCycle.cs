namespace CoinBot.Domain.Entities;

public sealed class MarketScannerCycle : BaseEntity
{
    public DateTime StartedAtUtc { get; set; }

    public DateTime CompletedAtUtc { get; set; }

    public string UniverseSource { get; set; } = string.Empty;

    public int ScannedSymbolCount { get; set; }

    public int EligibleCandidateCount { get; set; }

    public int TopCandidateCount { get; set; }

    public string? BestCandidateSymbol { get; set; }

    public decimal? BestCandidateScore { get; set; }

    public string? Summary { get; set; }
}
