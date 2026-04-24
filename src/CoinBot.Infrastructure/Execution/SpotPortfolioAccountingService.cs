using CoinBot.Application.Abstractions.ExchangeCredentials;
using CoinBot.Application.Abstractions.MarketData;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Exchange;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CoinBot.Infrastructure.Execution;

public sealed class SpotPortfolioAccountingService(
    ApplicationDbContext dbContext,
    IExchangeCredentialService exchangeCredentialService,
    IBinanceSpotPrivateRestClient spotPrivateRestClient,
    IMarketDataService marketDataService,
    ILogger<SpotPortfolioAccountingService> logger) : ISpotPortfolioAccountingService
{
    private const decimal PrecisionEpsilon = 0.000000000000000001m;
    private const string SystemActor = "system:spot-portfolio-accounting";

    public async Task<SpotPortfolioApplyResult?> ApplyAsync(
        ExecutionOrder order,
        BinanceOrderStatusSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(snapshot);

        if (order.Plane != ExchangeDataPlane.Spot ||
            order.ExecutionEnvironment != ExecutionEnvironment.Live ||
            !order.ExchangeAccountId.HasValue ||
            order.FilledQuantity <= 0m)
        {
            return null;
        }

        var credentialAccess = await exchangeCredentialService.GetAsync(
            new ExchangeCredentialAccessRequest(
                order.ExchangeAccountId.Value,
                SystemActor,
                ExchangeCredentialAccessPurpose.Synchronization,
                order.RootCorrelationId),
            cancellationToken);
        var fills = await spotPrivateRestClient.GetTradeFillsAsync(
            new BinanceOrderQueryRequest(
                order.ExchangeAccountId.Value,
                order.Symbol,
                string.IsNullOrWhiteSpace(order.ExternalOrderId) ? snapshot.ExchangeOrderId : order.ExternalOrderId,
                snapshot.ClientOrderId,
                credentialAccess.ApiKey,
                credentialAccess.ApiSecret,
                CommandId: ExecutionClientOrderId.Create(order.Id),
                CorrelationId: order.RootCorrelationId,
                ExecutionAttemptId: order.Id.ToString("N"),
                ExecutionOrderId: order.Id,
                UserId: order.OwnerUserId),
            cancellationToken);

        if (fills.Count == 0)
        {
            return SpotPortfolioApplyResult.Empty;
        }

        var orderedFills = fills
            .OrderBy(fill => fill.EventTimeUtc)
            .ThenBy(fill => fill.TradeId)
            .ToArray();
        var tradeIds = orderedFills.Select(fill => fill.TradeId).ToArray();
        var existingTradeIds = await dbContext.SpotPortfolioFills
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(entity =>
                entity.ExchangeAccountId == order.ExchangeAccountId.Value &&
                entity.Symbol == order.Symbol &&
                tradeIds.Contains(entity.TradeId) &&
                !entity.IsDeleted)
            .Select(entity => entity.TradeId)
            .ToListAsync(cancellationToken);
        var existingTradeIdSet = existingTradeIds.ToHashSet();
        var lastPersistedFill = await dbContext.SpotPortfolioFills
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(entity =>
                entity.ExchangeAccountId == order.ExchangeAccountId.Value &&
                entity.Symbol == order.Symbol &&
                !entity.IsDeleted)
            .OrderByDescending(entity => entity.OccurredAtUtc)
            .ThenByDescending(entity => entity.TradeId)
            .FirstOrDefaultAsync(cancellationToken);

        var state = lastPersistedFill is null
            ? SpotHoldingState.Empty
            : new SpotHoldingState(
                lastPersistedFill.HoldingQuantityAfter,
                lastPersistedFill.HoldingCostBasisAfter,
                lastPersistedFill.HoldingAverageCostAfter,
                lastPersistedFill.CumulativeRealizedPnlAfter,
                lastPersistedFill.CumulativeFeesInQuoteAfter);

        var newRows = new List<SpotPortfolioFill>();
        var duplicateTradeCount = 0;
        var realizedPnlDelta = 0m;
        var feesInQuoteApplied = 0m;

        foreach (var fill in orderedFills)
        {
            if (existingTradeIdSet.Contains(fill.TradeId))
            {
                duplicateTradeCount++;
                continue;
            }

            var feeAmountInQuote = await ResolveFeeAmountInQuoteAsync(
                fill.FeeAsset,
                fill.FeeAmount,
                order.BaseAsset,
                order.QuoteAsset,
                fill.Price,
                cancellationToken);
            var outcome = order.Side == ExecutionOrderSide.Buy
                ? ApplyBuyFill(state, fill, feeAmountInQuote, order.BaseAsset)
                : ApplySellFill(state, fill, feeAmountInQuote, order.BaseAsset);

            state = outcome.NextState;
            realizedPnlDelta = NormalizeDecimal(realizedPnlDelta + outcome.RealizedPnlDelta);
            feesInQuoteApplied = NormalizeDecimal(feesInQuoteApplied + feeAmountInQuote);

            newRows.Add(new SpotPortfolioFill
            {
                Id = Guid.NewGuid(),
                OwnerUserId = order.OwnerUserId,
                ExchangeAccountId = order.ExchangeAccountId.Value,
                ExecutionOrderId = order.Id,
                Plane = ExchangeDataPlane.Spot,
                Symbol = order.Symbol,
                BaseAsset = order.BaseAsset,
                QuoteAsset = order.QuoteAsset,
                Side = order.Side,
                ExchangeOrderId = fill.ExchangeOrderId,
                ClientOrderId = fill.ClientOrderId,
                TradeId = fill.TradeId,
                Quantity = fill.Quantity,
                QuoteQuantity = fill.QuoteQuantity,
                Price = fill.Price,
                FeeAsset = fill.FeeAsset,
                FeeAmount = fill.FeeAmount,
                FeeAmountInQuote = feeAmountInQuote,
                RealizedPnlDelta = outcome.RealizedPnlDelta,
                HoldingQuantityAfter = state.Quantity,
                HoldingCostBasisAfter = state.CostBasis,
                HoldingAverageCostAfter = state.AverageCost,
                CumulativeRealizedPnlAfter = state.RealizedPnl,
                CumulativeFeesInQuoteAfter = state.TotalFeesInQuote,
                Source = fill.Source,
                RootCorrelationId = order.RootCorrelationId,
                OccurredAtUtc = fill.EventTimeUtc
            });
        }

        if (newRows.Count > 0)
        {
            dbContext.SpotPortfolioFills.AddRange(newRows);
            logger.LogInformation(
                "Spot portfolio accounting applied {AppliedTradeCount} new trade fills for execution order {ExecutionOrderId}.",
                newRows.Count,
                order.Id);
        }

        return new SpotPortfolioApplyResult(
            newRows.Count,
            duplicateTradeCount,
            realizedPnlDelta,
            feesInQuoteApplied,
            state.Quantity,
            state.CostBasis,
            state.AverageCost,
            newRows.Count == 0
                ? string.Join(",", orderedFills.Select(fill => fill.TradeId.ToString(System.Globalization.CultureInfo.InvariantCulture)))
                : string.Join(",", newRows.Select(fill => fill.TradeId.ToString(System.Globalization.CultureInfo.InvariantCulture))),
            newRows.Count == 0
                ? orderedFills.Last().EventTimeUtc
                : newRows.Max(fill => fill.OccurredAtUtc));
    }

    private async Task<decimal> ResolveFeeAmountInQuoteAsync(
        string? feeAsset,
        decimal? feeAmount,
        string baseAsset,
        string quoteAsset,
        decimal tradePrice,
        CancellationToken cancellationToken)
    {
        var normalizedFeeAmount = feeAmount.GetValueOrDefault();

        if (normalizedFeeAmount == 0m || string.IsNullOrWhiteSpace(feeAsset))
        {
            return 0m;
        }

        if (string.Equals(feeAsset, quoteAsset, StringComparison.Ordinal))
        {
            return NormalizeDecimal(normalizedFeeAmount);
        }

        if (string.Equals(feeAsset, baseAsset, StringComparison.Ordinal))
        {
            return NormalizeDecimal(normalizedFeeAmount * tradePrice);
        }

        var directSymbol = $"{feeAsset}{quoteAsset}";
        var directPrice = await marketDataService.GetLatestPriceAsync(directSymbol, cancellationToken);
        if (directPrice is not null && directPrice.Price > 0m)
        {
            return NormalizeDecimal(normalizedFeeAmount * directPrice.Price);
        }

        var inverseSymbol = $"{quoteAsset}{feeAsset}";
        var inversePrice = await marketDataService.GetLatestPriceAsync(inverseSymbol, cancellationToken);
        if (inversePrice is not null && inversePrice.Price > 0m)
        {
            return NormalizeDecimal(normalizedFeeAmount / inversePrice.Price);
        }

        throw new InvalidOperationException(
            $"Spot fee conversion for asset '{feeAsset}' into quote asset '{quoteAsset}' is unavailable.");
    }

    private static SpotFillOutcome ApplyBuyFill(
        SpotHoldingState currentState,
        BinanceSpotTradeFillSnapshot fill,
        decimal feeAmountInQuote,
        string baseAsset)
    {
        var baseFee = string.Equals(fill.FeeAsset, baseAsset, StringComparison.Ordinal)
            ? fill.FeeAmount.GetValueOrDefault()
            : 0m;
        var netBaseQuantity = NormalizeDecimal(fill.Quantity - baseFee);

        if (netBaseQuantity <= 0m)
        {
            throw new InvalidOperationException("Spot buy fill must increase holding quantity.");
        }

        var costIncrease = NormalizeDecimal(fill.QuoteQuantity + feeAmountInQuote - (baseFee > 0m ? feeAmountInQuote : 0m));
        var nextQuantity = NormalizeDecimal(currentState.Quantity + netBaseQuantity);
        var nextCostBasis = NormalizeDecimal(currentState.CostBasis + costIncrease);
        var nextAverageCost = nextQuantity == 0m
            ? 0m
            : NormalizeDecimal(nextCostBasis / nextQuantity);
        var nextState = new SpotHoldingState(
            nextQuantity,
            nextCostBasis,
            nextAverageCost,
            currentState.RealizedPnl,
            NormalizeDecimal(currentState.TotalFeesInQuote + feeAmountInQuote));

        return new SpotFillOutcome(nextState, 0m);
    }

    private static SpotFillOutcome ApplySellFill(
        SpotHoldingState currentState,
        BinanceSpotTradeFillSnapshot fill,
        decimal feeAmountInQuote,
        string baseAsset)
    {
        var baseFee = string.Equals(fill.FeeAsset, baseAsset, StringComparison.Ordinal)
            ? fill.FeeAmount.GetValueOrDefault()
            : 0m;
        var totalBaseDebit = NormalizeDecimal(fill.Quantity + baseFee);

        if (currentState.Quantity + PrecisionEpsilon < totalBaseDebit)
        {
            throw new InvalidOperationException("Spot sell fill exceeds the locally costed holding quantity.");
        }

        var averageCostBefore = currentState.Quantity == 0m
            ? 0m
            : currentState.CostBasis / currentState.Quantity;
        var costRelief = NormalizeDecimal(averageCostBefore * totalBaseDebit);
        var netQuoteProceeds = NormalizeDecimal(fill.QuoteQuantity - (baseFee == 0m ? feeAmountInQuote : 0m));
        var realizedPnlDelta = NormalizeDecimal(netQuoteProceeds - costRelief);
        var nextQuantity = NormalizeDecimal(currentState.Quantity - totalBaseDebit);
        var nextCostBasis = nextQuantity == 0m
            ? 0m
            : NormalizeDecimal(currentState.CostBasis - costRelief);
        var nextAverageCost = nextQuantity == 0m
            ? 0m
            : NormalizeDecimal(nextCostBasis / nextQuantity);
        var nextState = new SpotHoldingState(
            nextQuantity,
            nextCostBasis,
            nextAverageCost,
            NormalizeDecimal(currentState.RealizedPnl + realizedPnlDelta),
            NormalizeDecimal(currentState.TotalFeesInQuote + feeAmountInQuote));

        return new SpotFillOutcome(nextState, realizedPnlDelta);
    }

    private static decimal NormalizeDecimal(decimal value)
    {
        return Math.Abs(value) <= PrecisionEpsilon
            ? 0m
            : value;
    }

    private sealed record SpotHoldingState(
        decimal Quantity,
        decimal CostBasis,
        decimal AverageCost,
        decimal RealizedPnl,
        decimal TotalFeesInQuote)
    {
        public static SpotHoldingState Empty { get; } = new(0m, 0m, 0m, 0m, 0m);
    }

    private sealed record SpotFillOutcome(
        SpotHoldingState NextState,
        decimal RealizedPnlDelta);
}

public sealed record SpotPortfolioApplyResult(
    int AppliedTradeCount,
    int DuplicateTradeCount,
    decimal RealizedPnlDelta,
    decimal FeesInQuoteApplied,
    decimal HoldingQuantityAfter,
    decimal HoldingCostBasisAfter,
    decimal HoldingAverageCostAfter,
    string TradeIdsSummary,
    DateTime? LastTradeAtUtc)
{
    public static SpotPortfolioApplyResult Empty { get; } = new(
        0,
        0,
        0m,
        0m,
        0m,
        0m,
        0m,
        string.Empty,
        null);
}
