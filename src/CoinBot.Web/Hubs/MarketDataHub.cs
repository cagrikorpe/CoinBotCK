using CoinBot.Application.Abstractions.MarketData;
using CoinBot.Application.Abstractions.Monitoring;
using CoinBot.Infrastructure.MarketData;
using CoinBot.Web.ViewModels.Home;
using CoinBot.Application.Abstractions.Indicators;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace CoinBot.Web.Hubs;

[Authorize]
public sealed class MarketDataHub(
    IMarketDataService marketDataService,
    ISharedSymbolRegistry symbolRegistry,
    ILogger<MarketDataHub> logger,
    IMonitoringTelemetryCollector? monitoringTelemetryCollector = null) : Hub
{
    public async Task<IReadOnlyCollection<DashboardMarketTickerViewModel>> SubscribeSymbolsAsync(
        IEnumerable<string> symbols)
    {
        var cancellationToken = Context.ConnectionAborted;
        var normalizedSymbols = NormalizeMany(symbols);

        if (normalizedSymbols.Count == 0)
        {
            return Array.Empty<DashboardMarketTickerViewModel>();
        }

        await marketDataService.TrackSymbolsAsync(normalizedSymbols, cancellationToken);

        foreach (var symbol in normalizedSymbols)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, CreateGroupName(symbol), cancellationToken);
        }

        var snapshots = new List<DashboardMarketTickerViewModel>(normalizedSymbols.Count);

        foreach (var symbol in normalizedSymbols)
        {
            var latestPrice = await marketDataService.GetLatestPriceAsync(symbol, cancellationToken);
            var metadata = await symbolRegistry.GetSymbolAsync(symbol, cancellationToken);
            snapshots.Add(CreateSnapshot(symbol, latestPrice, metadata));
        }

        logger.LogDebug(
            "MarketDataHub subscribed connection {ConnectionId} to {SymbolCount} symbols.",
            Context.ConnectionId,
            normalizedSymbols.Count);

        return snapshots;
    }

    public override async Task OnConnectedAsync()
    {
        monitoringTelemetryCollector?.AdjustSignalRConnectionCount(1);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        monitoringTelemetryCollector?.AdjustSignalRConnectionCount(-1);
        await base.OnDisconnectedAsync(exception);
    }

    public async Task<MarketChartSeedSnapshot> GetChartSeedAsync(
        string symbol,
        string timeframe,
        int candleCount = 240)
    {
        var services = Context.GetHttpContext()?.RequestServices
            ?? throw new InvalidOperationException("MarketDataHub requires an active HttpContext service scope.");
        var cancellationToken = Context.ConnectionAborted;
        var historicalGapFillerService = services.GetRequiredService<HistoricalGapFillerService>();
        var indicatorDataService = services.GetRequiredService<IIndicatorDataService>();
        var normalizedSymbol = NormalizeSymbol(symbol);
        var normalizedTimeframe = timeframe?.Trim();

        if (string.IsNullOrWhiteSpace(normalizedTimeframe))
        {
            throw new HubException("The timeframe is required.");
        }

        var boundedCandleCount = Math.Max(1, candleCount);

        await marketDataService.TrackSymbolAsync(normalizedSymbol, cancellationToken);
        await historicalGapFillerService.BackfillAsync([normalizedSymbol], cancellationToken);
        var candles = await historicalGapFillerService.LoadRecentCandlesAsync(
            normalizedSymbol,
            normalizedTimeframe,
            boundedCandleCount,
            cancellationToken);
        await indicatorDataService.PrimeAsync(
            normalizedSymbol,
            normalizedTimeframe,
            candles,
            cancellationToken);
        var indicatorSnapshot = await indicatorDataService.GetLatestAsync(
            normalizedSymbol,
            normalizedTimeframe,
            cancellationToken);

        return new MarketChartSeedSnapshot(
            normalizedSymbol,
            normalizedTimeframe,
            candles,
            indicatorSnapshot);
    }

    internal static DashboardMarketTickerViewModel CreateSnapshot(
        string symbol,
        MarketPriceSnapshot? latestPrice,
        SymbolMetadataSnapshot? metadata)
    {
        var normalizedSymbol = NormalizeSymbol(symbol);

        return new DashboardMarketTickerViewModel(
            Symbol: normalizedSymbol,
            BaseAsset: metadata?.BaseAsset ?? normalizedSymbol,
            QuoteAsset: metadata?.QuoteAsset ?? string.Empty,
            Price: latestPrice?.Price,
            ObservedAtUtc: latestPrice?.ObservedAtUtc,
            ReceivedAtUtc: latestPrice?.ReceivedAtUtc,
            Source: string.IsNullOrWhiteSpace(latestPrice?.Source)
                ? "MarketData.Pending"
                : latestPrice.Source,
            TickSize: metadata?.TickSize,
            StepSize: metadata?.StepSize,
            TradingStatus: string.IsNullOrWhiteSpace(metadata?.TradingStatus)
                ? "UNKNOWN"
                : metadata.TradingStatus,
            IsTradingEnabled: metadata?.IsTradingEnabled ?? false);
    }

    internal static string CreateGroupName(string symbol)
    {
        return $"market-data:{NormalizeSymbol(symbol)}";
    }

    internal static IReadOnlyCollection<string> NormalizeMany(IEnumerable<string> symbols)
    {
        ArgumentNullException.ThrowIfNull(symbols);

        var normalizedSymbols = new HashSet<string>(StringComparer.Ordinal);

        foreach (var symbol in symbols)
        {
            normalizedSymbols.Add(NormalizeSymbol(symbol));
        }

        return normalizedSymbols
            .OrderBy(symbol => symbol, StringComparer.Ordinal)
            .ToArray();
    }

    private static string NormalizeSymbol(string? symbol)
    {
        var normalizedSymbol = symbol?.Trim().ToUpperInvariant();

        if (string.IsNullOrWhiteSpace(normalizedSymbol))
        {
            throw new ArgumentException("The symbol is required.", nameof(symbol));
        }

        return normalizedSymbol;
    }
}
