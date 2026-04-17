using CoinBot.Domain.Entities;

namespace CoinBot.Infrastructure.MarketData;

internal static class MarketScannerCandidateIntegrityGuard
{
    internal const string LegacyArchivedDirtyMarketScoreReason = "LegacyArchivedDirtyMarketScore";

    internal static bool HasLegacyDirtyMarketScore(MarketScannerCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return HasLegacyDirtyMarketScore(candidate.MarketScore, candidate.QuoteVolume24h);
    }

    internal static bool HasLegacyDirtyMarketScore(decimal marketScore, decimal? quoteVolume24h)
    {
        if (marketScore > 100m)
        {
            return true;
        }

        return quoteVolume24h.HasValue &&
               quoteVolume24h.Value > 100m &&
               marketScore == quoteVolume24h.Value;
    }
}
