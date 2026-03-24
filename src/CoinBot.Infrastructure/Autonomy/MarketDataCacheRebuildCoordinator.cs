using CoinBot.Application.Abstractions.Autonomy;
using CoinBot.Application.Abstractions.MarketData;
using CoinBot.Infrastructure.MarketData;
using Microsoft.Extensions.Logging;

namespace CoinBot.Infrastructure.Autonomy;

public sealed class MarketDataCacheRebuildCoordinator(
    SharedSymbolRegistry symbolRegistry,
    ISharedSymbolRegistry sharedSymbolRegistry,
    IBinanceExchangeInfoClient exchangeInfoClient,
    ILogger<MarketDataCacheRebuildCoordinator> logger) : ICacheRebuildCoordinator
{
    public async Task<bool> RebuildAsync(
        string? symbol,
        string reason,
        CancellationToken cancellationToken = default)
    {
        _ = reason;

        IReadOnlyCollection<string> symbols;

        if (!string.IsNullOrWhiteSpace(symbol))
        {
            symbols = [symbol.Trim().ToUpperInvariant()];
        }
        else
        {
            symbols = (await sharedSymbolRegistry.ListSymbolsAsync(cancellationToken))
                .Select(item => item.Symbol)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(item => item, StringComparer.Ordinal)
                .ToArray();
        }

        if (symbols.Count == 0)
        {
            return true;
        }

        var metadata = await exchangeInfoClient.GetSymbolMetadataAsync(symbols, cancellationToken);

        if (metadata.Count == 0)
        {
            return false;
        }

        symbolRegistry.Upsert(metadata);

        logger.LogInformation(
            "Market data cache rebuild refreshed {SymbolCount} symbol metadata entries.",
            metadata.Count);

        return true;
    }
}
