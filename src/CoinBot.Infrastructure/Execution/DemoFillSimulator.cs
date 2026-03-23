using System.Globalization;
using CoinBot.Application.Abstractions.MarketData;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CoinBot.Infrastructure.Execution;

public sealed class DemoFillSimulator(
    IMarketDataService marketDataService,
    IOptions<DemoFillSimulatorOptions> options,
    TimeProvider timeProvider,
    ILogger<DemoFillSimulator> logger)
{
    private readonly DemoFillSimulatorOptions optionsValue = options.Value;

    public async Task<DemoSubmissionSimulation> SimulateOnSubmissionAsync(
        ExecutionOrder order,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(order);

        var marketPrice = await GetLatestPriceAsync(order.Symbol, cancellationToken);
        var metadata = await marketDataService.GetSymbolMetadataAsync(order.Symbol, cancellationToken);
        var remainingQuantity = GetRemainingQuantity(order);

        if (remainingQuantity <= 0m)
        {
            return new DemoSubmissionSimulation(null, null);
        }

        var fill = await BuildFillSimulationAsync(
            order,
            remainingQuantity,
            marketPrice,
            metadata,
            requireNewObservation: false,
            allowMarketFallback: true,
            cancellationToken);

        return new DemoSubmissionSimulation(
            BuildReservationPlan(order, remainingQuantity, fill),
            fill);
    }

    public async Task<DemoFillSimulation?> SimulateOnNextPriceAsync(
        ExecutionOrder order,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(order);

        var marketPrice = await GetLatestPriceAsync(order.Symbol, cancellationToken);

        if (marketPrice is null)
        {
            return null;
        }

        var remainingQuantity = GetRemainingQuantity(order);

        if (remainingQuantity <= 0m)
        {
            return null;
        }

        var metadata = await marketDataService.GetSymbolMetadataAsync(order.Symbol, cancellationToken);
        return await BuildFillSimulationAsync(
            order,
            remainingQuantity,
            marketPrice,
            metadata,
            requireNewObservation: true,
            allowMarketFallback: false,
            cancellationToken);
    }

    public DemoProtectiveTriggerKind EvaluateProtectiveTrigger(ExecutionOrder order, decimal observedPrice)
    {
        ArgumentNullException.ThrowIfNull(order);

        if (observedPrice <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(observedPrice), "Observed price must be greater than zero.");
        }

        var stopTriggered = order.StopLossPrice.HasValue && IsStopTriggered(order.Side, observedPrice, order.StopLossPrice.Value);
        var takeProfitTriggered = order.TakeProfitPrice.HasValue && IsTakeProfitTriggered(order.Side, observedPrice, order.TakeProfitPrice.Value);

        if (stopTriggered)
        {
            return DemoProtectiveTriggerKind.StopLoss;
        }

        return takeProfitTriggered
            ? DemoProtectiveTriggerKind.TakeProfit
            : DemoProtectiveTriggerKind.None;
    }

    private async Task<DemoFillSimulation?> BuildFillSimulationAsync(
        ExecutionOrder order,
        decimal remainingQuantity,
        MarketPriceSnapshot? marketPrice,
        SymbolMetadataSnapshot? metadata,
        bool requireNewObservation,
        bool allowMarketFallback,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (order.OrderType == ExecutionOrderType.Market)
        {
            return BuildMarketFillSimulation(order, remainingQuantity, marketPrice, metadata, allowMarketFallback);
        }

        if (marketPrice is null)
        {
            logger.LogDebug(
                "Demo limit order {ExecutionOrderId} is waiting for the first market price observation.",
                order.Id);

            return null;
        }

        if (requireNewObservation &&
            order.LastFilledAtUtc.HasValue &&
            marketPrice.ObservedAtUtc <= order.LastFilledAtUtc.Value)
        {
            return null;
        }

        if (!IsLimitTriggered(order.Side, marketPrice.Price, order.Price))
        {
            return null;
        }

        var feeRate = ResolveFeeRate(order.OrderType);
        var remainingNotional = remainingQuantity * order.Price;
        var shouldPartialFill = order.FilledQuantity == 0m &&
                                remainingNotional >= optionsValue.PartialFillMinNotional;
        var fillQuantity = shouldPartialFill
            ? ResolvePartialFillQuantity(remainingQuantity, metadata?.StepSize)
            : remainingQuantity;
        var fillPrice = order.Price;
        var feeAmount = fillQuantity * fillPrice * feeRate;
        var detail = BuildDetail(
            order.OrderType,
            order.Side,
            triggerReason: "LimitTriggered",
            marketPrice.Price,
            marketPrice.Source,
            fillQuantity,
            fillPrice,
            feeRate,
            slippageBps: 0);

        return new DemoFillSimulation(
            marketPrice.Price,
            marketPrice.Source,
            marketPrice.ObservedAtUtc,
            fillQuantity,
            fillPrice,
            order.QuoteAsset,
            feeRate,
            feeAmount,
            feeAmount,
            IsFinalFill: fillQuantity >= remainingQuantity,
            EventCode: fillQuantity >= remainingQuantity
                ? "DemoFilled"
                : "DemoPartiallyFilled",
            Detail: detail);
    }

    private DemoFillSimulation? BuildMarketFillSimulation(
        ExecutionOrder order,
        decimal remainingQuantity,
        MarketPriceSnapshot? marketPrice,
        SymbolMetadataSnapshot? metadata,
        bool allowMarketFallback)
    {
        if (marketPrice is null && !allowMarketFallback)
        {
            return null;
        }

        var referencePrice = marketPrice?.Price ?? order.Price;
        var referenceSource = marketPrice?.Source ?? "ExecutionOrder.PriceFallback";
        var observedAtUtc = marketPrice?.ObservedAtUtc ?? NormalizeTimestamp(timeProvider.GetUtcNow().UtcDateTime);
        var slippageBps = ResolveMarketOrderSlippageBps(remainingQuantity * referencePrice, marketPrice is null);
        var slippageRate = slippageBps / 10000m;
        var unroundedFillPrice = order.Side == ExecutionOrderSide.Buy
            ? referencePrice * (1m + slippageRate)
            : referencePrice * (1m - slippageRate);
        var fillPrice = metadata is null || metadata.TickSize <= 0m
            ? unroundedFillPrice
            : RoundPriceAdversely(unroundedFillPrice, order.Side, metadata.TickSize);
        var feeRate = ResolveFeeRate(order.OrderType);
        var feeAmount = remainingQuantity * fillPrice * feeRate;

        return new DemoFillSimulation(
            referencePrice,
            referenceSource,
            observedAtUtc,
            remainingQuantity,
            fillPrice,
            order.QuoteAsset,
            feeRate,
            feeAmount,
            feeAmount,
            IsFinalFill: true,
            EventCode: "DemoFilled",
            Detail: BuildDetail(
                order.OrderType,
                order.Side,
                triggerReason: "MarketAccepted",
                referencePrice,
                referenceSource,
                remainingQuantity,
                fillPrice,
                feeRate,
                slippageBps));
    }

    private DemoFillReservationPlan? BuildReservationPlan(
        ExecutionOrder order,
        decimal remainingQuantity,
        DemoFillSimulation? fill)
    {
        if (remainingQuantity <= 0m)
        {
            return null;
        }

        if (order.Side == ExecutionOrderSide.Sell)
        {
            var consumedBaseQuantity = fill?.FillQuantity ?? 0m;

            return new DemoFillReservationPlan(
                order.BaseAsset,
                remainingQuantity,
                consumedBaseQuantity,
                "Spot sell demo orders reserve the remaining base quantity.");
        }

        var feeRate = ResolveFeeRate(order.OrderType);
        var reservationAmount = order.OrderType == ExecutionOrderType.Market && fill is not null
            ? (fill.FillQuantity * fill.FillPrice) + fill.FeeAmountInQuote
            : remainingQuantity * order.Price * (1m + feeRate);
        var consumedReservedAmount = fill is null
            ? 0m
            : order.OrderType == ExecutionOrderType.Market
                ? reservationAmount
                : fill.FillQuantity * order.Price * (1m + feeRate);

        return new DemoFillReservationPlan(
            order.QuoteAsset,
            reservationAmount,
            consumedReservedAmount,
            "Spot buy demo orders reserve quote notional plus quote-side fees.");
    }

    private Task<MarketPriceSnapshot?> GetLatestPriceAsync(string symbol, CancellationToken cancellationToken)
    {
        return marketDataService.GetLatestPriceAsync(symbol, cancellationToken).AsTask();
    }

    private static decimal GetRemainingQuantity(ExecutionOrder order)
    {
        return Math.Max(0m, order.Quantity - order.FilledQuantity);
    }

    private static bool IsLimitTriggered(ExecutionOrderSide side, decimal observedPrice, decimal limitPrice)
    {
        return side == ExecutionOrderSide.Buy
            ? observedPrice <= limitPrice
            : observedPrice >= limitPrice;
    }

    private static bool IsStopTriggered(ExecutionOrderSide side, decimal observedPrice, decimal stopLossPrice)
    {
        return side == ExecutionOrderSide.Buy
            ? observedPrice <= stopLossPrice
            : observedPrice >= stopLossPrice;
    }

    private static bool IsTakeProfitTriggered(ExecutionOrderSide side, decimal observedPrice, decimal takeProfitPrice)
    {
        return side == ExecutionOrderSide.Buy
            ? observedPrice >= takeProfitPrice
            : observedPrice <= takeProfitPrice;
    }

    private decimal ResolveFeeRate(ExecutionOrderType orderType)
    {
        return (orderType == ExecutionOrderType.Market
                ? optionsValue.TakerFeeBps
                : optionsValue.MakerFeeBps) /
            10000m;
    }

    private int ResolveMarketOrderSlippageBps(decimal notional, bool usedFallbackPrice)
    {
        var sizeImpactSteps = optionsValue.SizeImpactNotionalStep <= 0m
            ? 0
            : (int)decimal.Floor(notional / optionsValue.SizeImpactNotionalStep);
        var slippageBps = optionsValue.MarketOrderBaseSlippageBps +
                          (sizeImpactSteps * optionsValue.SizeImpactStepBps);

        if (usedFallbackPrice)
        {
            slippageBps += optionsValue.MissingPriceFallbackPenaltyBps;
        }

        return Math.Min(slippageBps, optionsValue.MaxMarketOrderSlippageBps);
    }

    private decimal ResolvePartialFillQuantity(decimal remainingQuantity, decimal? stepSize)
    {
        var candidate = remainingQuantity * optionsValue.PartialFillRatio;

        if (!stepSize.HasValue || stepSize.Value <= 0m)
        {
            return candidate <= 0m || candidate >= remainingQuantity
                ? remainingQuantity
                : candidate;
        }

        var rounded = RoundDownToStep(candidate, stepSize.Value);

        if (rounded <= 0m || rounded >= remainingQuantity)
        {
            return remainingQuantity;
        }

        return rounded;
    }

    private static decimal RoundPriceAdversely(decimal price, ExecutionOrderSide side, decimal tickSize)
    {
        if (tickSize <= 0m)
        {
            return price;
        }

        var steps = price / tickSize;
        var roundedSteps = side == ExecutionOrderSide.Buy
            ? decimal.Ceiling(steps)
            : decimal.Floor(steps);

        return roundedSteps * tickSize;
    }

    private static decimal RoundDownToStep(decimal quantity, decimal stepSize)
    {
        if (stepSize <= 0m)
        {
            return quantity;
        }

        return decimal.Floor(quantity / stepSize) * stepSize;
    }

    private static string BuildDetail(
        ExecutionOrderType orderType,
        ExecutionOrderSide side,
        string triggerReason,
        decimal referencePrice,
        string referenceSource,
        decimal fillQuantity,
        decimal fillPrice,
        decimal feeRate,
        int slippageBps)
    {
        var detail = FormattableString.Invariant(
            $"{triggerReason}; OrderType={orderType}; Side={side}; ReferencePrice={referencePrice:0.##################}; ReferenceSource={referenceSource}; FillQuantity={fillQuantity:0.##################}; FillPrice={fillPrice:0.##################}; FeeBps={feeRate * 10000m:0.##}; SlippageBps={slippageBps:0}");

        return detail.Length <= 512
            ? detail
            : detail[..512];
    }

    private static DateTime NormalizeTimestamp(DateTime timestamp)
    {
        return timestamp.Kind switch
        {
            DateTimeKind.Utc => timestamp,
            DateTimeKind.Local => timestamp.ToUniversalTime(),
            _ => DateTime.SpecifyKind(timestamp, DateTimeKind.Utc)
        };
    }
}
