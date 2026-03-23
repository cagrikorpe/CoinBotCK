using CoinBot.Application.Abstractions.MarketData;
using CoinBot.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace CoinBot.Infrastructure.DemoPortfolio;

public sealed class DemoWalletValuationService(
    IMarketDataService marketDataService,
    TimeProvider timeProvider,
    ILogger<DemoWalletValuationService> logger)
{
    private const string ReferenceQuoteAsset = "USDT";
    private const string QuoteParitySource = "VirtualWallet.ReferenceQuoteParity";
    private const string ZeroBalanceSource = "VirtualWallet.ZeroBalance";

    public async Task SyncAsync(DemoWallet wallet, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(wallet);

        await SyncAsync([wallet], cancellationToken);
    }

    public async Task SyncAsync(IEnumerable<DemoWallet> wallets, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(wallets);

        foreach (var wallet in wallets
                     .Where(static entity => entity is not null)
                     .Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await SyncWalletAsync(wallet, cancellationToken);
        }
    }

    private async Task SyncWalletAsync(DemoWallet wallet, CancellationToken cancellationToken)
    {
        var asset = NormalizeAsset(wallet.Asset);
        var observedAtUtc = timeProvider.GetUtcNow().UtcDateTime;

        wallet.ReferenceQuoteAsset = ReferenceQuoteAsset;

        if (string.Equals(asset, ReferenceQuoteAsset, StringComparison.Ordinal))
        {
            ApplyValuation(
                wallet,
                referenceSymbol: null,
                referencePrice: 1m,
                observedAtUtc,
                QuoteParitySource);
            return;
        }

        var referenceSymbol = $"{asset}{ReferenceQuoteAsset}";
        wallet.ReferenceSymbol = referenceSymbol;

        if (wallet.TotalBalance == 0m)
        {
            ApplyValuation(
                wallet,
                referenceSymbol,
                wallet.LastReferencePrice,
                observedAtUtc,
                ZeroBalanceSource);
            return;
        }

        await marketDataService.TrackSymbolAsync(referenceSymbol, cancellationToken);
        var priceSnapshot = await marketDataService.GetLatestPriceAsync(referenceSymbol, cancellationToken);

        if (priceSnapshot is null || priceSnapshot.Price <= 0m)
        {
            logger.LogDebug(
                "Virtual wallet valuation skipped for asset {Asset} because no latest market price was available for {Symbol}.",
                asset,
                referenceSymbol);
            return;
        }

        ApplyValuation(
            wallet,
            referenceSymbol,
            priceSnapshot.Price,
            NormalizeTimestamp(priceSnapshot.ObservedAtUtc),
            priceSnapshot.Source);
    }

    private static void ApplyValuation(
        DemoWallet wallet,
        string? referenceSymbol,
        decimal? referencePrice,
        DateTime observedAtUtc,
        string source)
    {
        var normalizedPrice = referencePrice.HasValue && referencePrice.Value < 0m
            ? 0m
            : referencePrice;

        wallet.ReferenceSymbol = referenceSymbol;
        wallet.ReferenceQuoteAsset = ReferenceQuoteAsset;
        wallet.LastReferencePrice = normalizedPrice;
        wallet.AvailableValueInReferenceQuote = normalizedPrice.HasValue
            ? wallet.AvailableBalance * normalizedPrice.Value
            : wallet.AvailableBalance == 0m
                ? 0m
                : null;
        wallet.ReservedValueInReferenceQuote = normalizedPrice.HasValue
            ? wallet.ReservedBalance * normalizedPrice.Value
            : wallet.ReservedBalance == 0m
                ? 0m
                : null;
        wallet.LastValuationAtUtc = observedAtUtc;
        wallet.LastValuationSource = source.Trim();
    }

    private static string NormalizeAsset(string asset)
    {
        return asset.Trim().ToUpperInvariant();
    }

    private static DateTime NormalizeTimestamp(DateTime value)
    {
        return value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
    }
}
