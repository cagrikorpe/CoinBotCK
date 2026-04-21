using System.Globalization;
using CoinBot.Application.Abstractions.Administration;
using CoinBot.Application.Abstractions.DemoPortfolio;
using CoinBot.Application.Abstractions.Execution;
using CoinBot.Application.Abstractions.MarketData;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CoinBot.Infrastructure.Execution;

public sealed class VirtualExecutionWatchdogService(
    ApplicationDbContext dbContext,
    IDemoPortfolioAccountingService demoPortfolioAccountingService,
    IExecutionEngine executionEngine,
    IMarketDataService marketDataService,
    IOptions<DemoFillSimulatorOptions> fillOptions,
    DemoFillSimulator demoFillSimulator,
    TimeProvider timeProvider,
    ILogger<VirtualExecutionWatchdogService> logger,
    ITraceService? traceService = null,
    IOptions<ExecutionRuntimeOptions>? executionRuntimeOptions = null)
{
    private const string SystemActor = "system:virtual-watchdog";
    private static readonly ExecutionOrderState[] OpenOrderStates =
    [
        ExecutionOrderState.Submitted,
        ExecutionOrderState.PartiallyFilled,
        ExecutionOrderState.CancelRequested
    ];

    private readonly DemoFillSimulatorOptions fillOptionsValue = fillOptions.Value;
    private readonly ExecutionRuntimeOptions executionRuntimeOptionsValue = executionRuntimeOptions?.Value ?? new ExecutionRuntimeOptions();

    public async Task<VirtualExecutionWatchdogRunResult> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        if (!executionRuntimeOptionsValue.AllowInternalDemoExecution)
        {
            return new VirtualExecutionWatchdogRunResult(0, 0, 0);
        }

        var openOrders = await dbContext.ExecutionOrders
            .IgnoreQueryFilters()
            .Where(entity =>
                !entity.IsDeleted &&
                entity.ExecutionEnvironment == ExecutionEnvironment.Demo &&
                entity.ExecutorKind == ExecutionOrderExecutorKind.Virtual &&
                OpenOrderStates.Contains(entity.State))
            .OrderBy(entity => entity.LastStateChangedAtUtc)
            .ToListAsync(cancellationToken);
        var positionSymbols = await dbContext.DemoPositions
            .IgnoreQueryFilters()
            .Where(entity =>
                !entity.IsDeleted &&
                entity.Quantity != 0m)
            .Select(entity => entity.Symbol)
            .Distinct()
            .ToListAsync(cancellationToken);
        var symbols = openOrders
            .Select(entity => entity.Symbol)
            .Concat(positionSymbols)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (symbols.Length > 0)
        {
            await marketDataService.TrackSymbolsAsync(symbols, cancellationToken);
        }

        var advancedOrderCount = 0;

        foreach (var order in openOrders)
        {
            if (await TryAdvanceOpenOrderAsync(order, cancellationToken))
            {
                advancedOrderCount++;
            }
        }

        var openPositions = await dbContext.DemoPositions
            .IgnoreQueryFilters()
            .Where(entity =>
                !entity.IsDeleted &&
                entity.Quantity != 0m)
            .OrderBy(entity => entity.OwnerUserId)
            .ThenBy(entity => entity.PositionScopeKey)
            .ThenBy(entity => entity.Symbol)
            .ToListAsync(cancellationToken);

        var repricedPositionCount = 0;
        var protectiveDispatchCount = 0;

        foreach (var position in openPositions)
        {
            var latestPrice = await marketDataService.GetLatestPriceAsync(position.Symbol, cancellationToken);

            if (latestPrice is null)
            {
                continue;
            }

            try
            {
                var markResult = await demoPortfolioAccountingService.UpdateMarkPriceAsync(
                    new DemoMarkPriceUpdateRequest(
                        position.OwnerUserId,
                        ExecutionEnvironment.Demo,
                        BuildMarkOperationId(position.Id, latestPrice.ObservedAtUtc),
                        position.Symbol,
                        position.BaseAsset,
                        position.QuoteAsset,
                        latestPrice.Price,
                        position.BotId,
                        latestPrice.ObservedAtUtc,
                        position.PositionKind,
                        LastPrice: latestPrice.Price),
                    cancellationToken);

                repricedPositionCount++;

                if (markResult.Position is null ||
                    markResult.Position.Quantity == 0m ||
                    markResult.Position.PositionKind != DemoPositionKind.Spot)
                {
                    continue;
                }

                if (await TryDispatchProtectiveCloseAsync(position, markResult.Position, latestPrice, cancellationToken))
                {
                    protectiveDispatchCount++;
                }
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception,
                    "Virtual execution watchdog failed while repricing demo position {PositionScopeKey}/{Symbol} for {OwnerUserId}.",
                    position.PositionScopeKey,
                    position.Symbol,
                    position.OwnerUserId);
            }
        }

        logger.LogInformation(
            "Virtual execution watchdog cycle completed. AdvancedOrders={AdvancedOrders}; RepricedPositions={RepricedPositions}; ProtectiveDispatches={ProtectiveDispatches}.",
            advancedOrderCount,
            repricedPositionCount,
            protectiveDispatchCount);

        return new VirtualExecutionWatchdogRunResult(
            advancedOrderCount,
            repricedPositionCount,
            protectiveDispatchCount);
    }

    private async Task<bool> TryAdvanceOpenOrderAsync(
        ExecutionOrder order,
        CancellationToken cancellationToken)
    {
        try
        {
            await dbContext.Entry(order).ReloadAsync(cancellationToken);

            if (!OpenOrderStates.Contains(order.State))
            {
                return false;
            }

            var simulation = await demoFillSimulator.SimulateOnNextPriceAsync(order, cancellationToken);

            if (simulation is null)
            {
                return false;
            }

            await ApplySimulationAsync(
                order,
                simulation,
                ResolveConsumedReservedAmount(order, simulation),
                cancellationToken);

            logger.LogInformation(
                "Virtual execution watchdog advanced order {ExecutionOrderId} to {ExecutionOrderState}.",
                order.Id,
                order.State);

            return true;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Virtual execution watchdog failed closed for order {ExecutionOrderId}.",
                order.Id);

            await dbContext.Entry(order).ReloadAsync(cancellationToken);

            if (!OpenOrderStates.Contains(order.State))
            {
                logger.LogInformation(
                    "Virtual execution watchdog skipped fail-closed transition for order {ExecutionOrderId} because the order is already {ExecutionOrderState}.",
                    order.Id,
                    order.State);
                return false;
            }

            await DiscardTrackedDemoAccountingChangesAsync(cancellationToken);
            await TryReleaseOutstandingReservationAsync(order, cancellationToken);
            order.FailureCode = ResolveFailureCode(exception);
            order.FailureDetail = Truncate(exception.Message, 512);

            var lastTransition = await GetLastTransitionAsync(order.Id, cancellationToken);
            await PersistTransitionAsync(
                order,
                ExecutionOrderState.Failed,
                "VirtualWatchdogFailedClosed",
                Truncate(exception.Message, 512),
                lastTransition?.CorrelationId ?? order.RootCorrelationId,
                cancellationToken);
            await UpdateBotOpenOrderCountAsync(order.BotId, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);

            return false;
        }
    }

    private async Task DiscardTrackedDemoAccountingChangesAsync(CancellationToken cancellationToken)
    {
        var entries = dbContext.ChangeTracker
            .Entries()
            .Where(entry => entry.Entity is DemoPosition or DemoWallet or DemoLedgerTransaction or DemoLedgerEntry)
            .ToArray();

        foreach (var entry in entries)
        {
            if (entry.State == EntityState.Added)
            {
                entry.State = EntityState.Detached;
                continue;
            }

            if (entry.State != EntityState.Unchanged)
            {
                await entry.ReloadAsync(cancellationToken);
            }
        }
    }

    private async Task ApplySimulationAsync(
        ExecutionOrder order,
        DemoFillSimulation simulation,
        decimal consumedReservedAmount,
        CancellationToken cancellationToken)
    {
        var lastTransition = await GetLastTransitionAsync(order.Id, cancellationToken);
        var parentCorrelationId = lastTransition?.CorrelationId ?? order.RootCorrelationId;

        await demoPortfolioAccountingService.ApplyFillAsync(
            new DemoFillAccountingRequest(
                order.OwnerUserId,
                ExecutionEnvironment.Demo,
                BuildFillOperationId(order.Id, lastTransition?.SequenceNumber ?? 0),
                order.Symbol,
                order.BaseAsset,
                order.QuoteAsset,
                MapTradeSide(order.Side),
                simulation.FillQuantity,
                simulation.FillPrice,
                consumedReservedAmount,
                order.BotId,
                BuildDemoOrderId(order.Id),
                BuildDemoFillId(order.Id, (lastTransition?.SequenceNumber ?? 0) + 1),
                simulation.FeeAsset,
                simulation.FeeAmount,
                simulation.FeeAmountInQuote,
                MarkPrice: ResolveDemoMarkPrice(simulation),
                OccurredAtUtc: simulation.ObservedAtUtc),
            cancellationToken);

        order.FailureCode = null;
        order.FailureDetail = null;
        order.AverageFillPrice = BlendAverageFillPrice(
            order.AverageFillPrice,
            order.FilledQuantity,
            simulation.FillPrice,
            simulation.FillQuantity);
        order.FilledQuantity += simulation.FillQuantity;
        order.LastFilledAtUtc = simulation.ObservedAtUtc;

        await PersistTransitionAsync(
            order,
            simulation.IsFinalFill
                ? ExecutionOrderState.Filled
                : ExecutionOrderState.PartiallyFilled,
            simulation.EventCode,
            AppendWatchdogDetail(AppendProtectiveRule(simulation.Detail, order)),
            parentCorrelationId,
            cancellationToken);
        await UpdateBotOpenOrderCountAsync(order.BotId, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await WriteInternalExecutionTraceAsync(
            order,
            endpoint: "internal://demo-fill-simulator/watchdog",
            requestMasked: BuildInternalExecutionTraceRequest(order, phase: "WatchdogFill"),
            responseMasked: BuildInternalExecutionTraceResponse(order, simulation),
            executionAttemptId: $"vw-fill:{order.Id:N}:{simulation.EventCode}",
            cancellationToken);
    }

    private async Task<bool> TryDispatchProtectiveCloseAsync(
        DemoPosition positionEntity,
        DemoPositionSnapshot position,
        MarketPriceSnapshot latestPrice,
        CancellationToken cancellationToken)
    {
        var sourceOrder = await ResolveProtectiveSourceOrderAsync(positionEntity, position, cancellationToken);

        if (sourceOrder is null)
        {
            return false;
        }

        var triggerKind = demoFillSimulator.EvaluateProtectiveTrigger(sourceOrder, latestPrice.Price);

        if (triggerKind == DemoProtectiveTriggerKind.None)
        {
            return false;
        }

        var dispatchResult = await executionEngine.DispatchAsync(
            new ExecutionCommand(
                Actor: SystemActor,
                OwnerUserId: sourceOrder.OwnerUserId,
                TradingStrategyId: sourceOrder.TradingStrategyId,
                TradingStrategyVersionId: sourceOrder.TradingStrategyVersionId,
                StrategySignalId: Guid.NewGuid(),
                SignalType: StrategySignalType.Exit,
                StrategyKey: sourceOrder.StrategyKey,
                Symbol: sourceOrder.Symbol,
                Timeframe: sourceOrder.Timeframe,
                BaseAsset: sourceOrder.BaseAsset,
                QuoteAsset: sourceOrder.QuoteAsset,
                Side: ResolveProtectiveExitSide(position.Quantity),
                OrderType: ExecutionOrderType.Market,
                Quantity: Math.Abs(position.Quantity),
                Price: latestPrice.Price,
                BotId: sourceOrder.BotId,
                ExchangeAccountId: sourceOrder.ExchangeAccountId,
                IsDemo: true,
                IdempotencyKey: BuildProtectiveIdempotencyKey(sourceOrder.Id, triggerKind, latestPrice.ObservedAtUtc),
                CorrelationId: CreateCorrelationId(),
                ParentCorrelationId: sourceOrder.RootCorrelationId,
                Context: BuildProtectiveContext(triggerKind, sourceOrder, latestPrice),
                ReduceOnly: true),
            cancellationToken);

        if (dispatchResult.IsDuplicate)
        {
            return false;
        }

        if (dispatchResult.Order.State is ExecutionOrderState.Rejected or ExecutionOrderState.Failed)
        {
            logger.LogWarning(
                "Virtual execution watchdog trigger {ProtectiveTrigger} created order {ExecutionOrderId} but it closed as {ExecutionOrderState}.",
                triggerKind,
                dispatchResult.Order.ExecutionOrderId,
                dispatchResult.Order.State);
        }
        else
        {
            logger.LogInformation(
                "Virtual execution watchdog dispatched {ProtectiveTrigger} close order {ExecutionOrderId} for source order {SourceExecutionOrderId}.",
                triggerKind,
                dispatchResult.Order.ExecutionOrderId,
                sourceOrder.Id);
        }

        return true;
    }

    private async Task<ExecutionOrder?> ResolveProtectiveSourceOrderAsync(
        DemoPosition positionEntity,
        DemoPositionSnapshot position,
        CancellationToken cancellationToken)
    {
        if (position.Quantity <= 0m || position.PositionKind != DemoPositionKind.Spot)
        {
            return null;
        }

        return await dbContext.ExecutionOrders
            .IgnoreQueryFilters()
            .Where(entity =>
                !entity.IsDeleted &&
                entity.OwnerUserId == positionEntity.OwnerUserId &&
                entity.BotId == position.BotId &&
                entity.SignalType == StrategySignalType.Entry &&
                entity.ExecutionEnvironment == ExecutionEnvironment.Demo &&
                entity.ExecutorKind == ExecutionOrderExecutorKind.Virtual &&
                entity.State == ExecutionOrderState.Filled &&
                entity.Symbol == position.Symbol &&
                entity.BaseAsset == position.BaseAsset &&
                entity.QuoteAsset == position.QuoteAsset &&
                entity.Side == ExecutionOrderSide.Buy &&
                (entity.StopLossPrice.HasValue || entity.TakeProfitPrice.HasValue))
            .OrderByDescending(entity => entity.LastFilledAtUtc ?? entity.LastStateChangedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task TryReleaseOutstandingReservationAsync(
        ExecutionOrder order,
        CancellationToken cancellationToken)
    {
        var remainingReservationAmount = CalculateOutstandingReservation(order);

        if (remainingReservationAmount <= 0m)
        {
            return;
        }

        try
        {
            await demoPortfolioAccountingService.ReleaseFundsAsync(
                new DemoFundsReleaseRequest(
                    order.OwnerUserId,
                    ExecutionEnvironment.Demo,
                    BuildReleaseOperationId(order.Id),
                    order.Side == ExecutionOrderSide.Buy
                        ? order.QuoteAsset
                        : order.BaseAsset,
                    remainingReservationAmount,
                    BuildDemoOrderId(order.Id),
                    timeProvider.GetUtcNow().UtcDateTime),
                cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Virtual execution watchdog failed while releasing outstanding reservation for order {ExecutionOrderId}.",
                order.Id);
        }
    }

    private async Task<ExecutionOrderTransition?> GetLastTransitionAsync(
        Guid executionOrderId,
        CancellationToken cancellationToken)
    {
        return await dbContext.ExecutionOrderTransitions
            .IgnoreQueryFilters()
            .Where(entity =>
                entity.ExecutionOrderId == executionOrderId &&
                !entity.IsDeleted)
            .OrderByDescending(entity => entity.SequenceNumber)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task PersistTransitionAsync(
        ExecutionOrder order,
        ExecutionOrderState targetState,
        string eventCode,
        string? detail,
        string? parentCorrelationId,
        CancellationToken cancellationToken)
    {
        var lastTransition = await GetLastTransitionAsync(order.Id, cancellationToken);
        var transition = ExecutionOrderStateMachine.Transition(
            order,
            sequenceNumber: (lastTransition?.SequenceNumber ?? 0) + 1,
            targetState,
            eventCode,
            NormalizeTimestamp(timeProvider.GetUtcNow().UtcDateTime),
            CreateCorrelationId(),
            parentCorrelationId,
            detail);

        dbContext.ExecutionOrderTransitions.Add(transition);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task UpdateBotOpenOrderCountAsync(Guid? botId, CancellationToken cancellationToken)
    {
        if (!botId.HasValue)
        {
            return;
        }

        var bot = await dbContext.TradingBots
            .IgnoreQueryFilters()
            .SingleOrDefaultAsync(
                entity => entity.Id == botId.Value &&
                          !entity.IsDeleted,
                cancellationToken);

        if (bot is null)
        {
            return;
        }

        bot.OpenOrderCount = await dbContext.ExecutionOrders
            .IgnoreQueryFilters()
            .CountAsync(
                entity => entity.BotId == botId.Value &&
                          !entity.IsDeleted &&
                          OpenOrderStates.Contains(entity.State),
                cancellationToken);
    }

    private decimal ResolveConsumedReservedAmount(ExecutionOrder order, DemoFillSimulation simulation)
    {
        return order.Side == ExecutionOrderSide.Buy
            ? (simulation.FillQuantity * order.Price) + simulation.FeeAmountInQuote
            : simulation.FillQuantity;
    }

    private decimal CalculateOutstandingReservation(ExecutionOrder order)
    {
        var remainingQuantity = Math.Max(0m, order.Quantity - order.FilledQuantity);

        if (remainingQuantity <= 0m)
        {
            return 0m;
        }

        if (order.Side == ExecutionOrderSide.Sell)
        {
            return remainingQuantity;
        }

        var feeRate = ResolveFeeRate(order.OrderType);
        return remainingQuantity * order.Price * (1m + feeRate);
    }

    private decimal ResolveFeeRate(ExecutionOrderType orderType)
    {
        return (orderType == ExecutionOrderType.Market
                ? fillOptionsValue.TakerFeeBps
                : fillOptionsValue.MakerFeeBps) /
            10000m;
    }

    private static DemoTradeSide MapTradeSide(ExecutionOrderSide side)
    {
        return side == ExecutionOrderSide.Buy
            ? DemoTradeSide.Buy
            : DemoTradeSide.Sell;
    }

    private static ExecutionOrderSide ResolveProtectiveExitSide(decimal quantity)
    {
        return quantity > 0m
            ? ExecutionOrderSide.Sell
            : ExecutionOrderSide.Buy;
    }

    private static decimal ResolveDemoMarkPrice(DemoFillSimulation fill)
    {
        return string.Equals(fill.ReferenceSource, "ExecutionOrder.PriceFallback", StringComparison.Ordinal)
            ? fill.FillPrice
            : fill.ReferencePrice;
    }

    private static decimal BlendAverageFillPrice(
        decimal? currentAverageFillPrice,
        decimal currentFilledQuantity,
        decimal fillPrice,
        decimal fillQuantity)
    {
        if (fillQuantity <= 0m)
        {
            return currentAverageFillPrice ?? 0m;
        }

        if (!currentAverageFillPrice.HasValue || currentFilledQuantity <= 0m)
        {
            return fillPrice;
        }

        var totalNotional = (currentAverageFillPrice.Value * currentFilledQuantity) + (fillPrice * fillQuantity);
        return totalNotional / (currentFilledQuantity + fillQuantity);
    }

    private static string BuildMarkOperationId(Guid positionId, DateTime observedAtUtc)
    {
        return $"vw-mark:{positionId:N}:{NormalizeTimestamp(observedAtUtc).Ticks}";
    }

    private static string BuildFillOperationId(Guid orderId, int lastSequenceNumber)
    {
        return $"vw-fill:{orderId:N}:{lastSequenceNumber + 1}";
    }

    private static string BuildReleaseOperationId(Guid orderId)
    {
        return $"vw-release:{orderId:N}";
    }

    private static string BuildDemoOrderId(Guid orderId)
    {
        return orderId.ToString("N");
    }

    private static string BuildDemoFillId(Guid orderId, int sequenceNumber)
    {
        return $"vw-demo-fill:{orderId:N}:{sequenceNumber}";
    }

    private static string BuildProtectiveIdempotencyKey(
        Guid sourceOrderId,
        DemoProtectiveTriggerKind triggerKind,
        DateTime observedAtUtc)
    {
        return $"vw-exit:{sourceOrderId:N}:{triggerKind}:{NormalizeTimestamp(observedAtUtc).Ticks}";
    }

    private static string BuildProtectiveContext(
        DemoProtectiveTriggerKind triggerKind,
        ExecutionOrder sourceOrder,
        MarketPriceSnapshot latestPrice)
    {
        var context = FormattableString.Invariant(
            $"VirtualExecutionWatchdog | Trigger={triggerKind} | SourceExecutionOrder={sourceOrder.Id:N} | ObservedPrice={latestPrice.Price:0.##################} | ObservedAtUtc={NormalizeTimestamp(latestPrice.ObservedAtUtc):O} | MarketSource={latestPrice.Source}");

        return context.Length <= 512
            ? context
            : context[..512];
    }

    private static string AppendProtectiveRule(string detail, ExecutionOrder order)
    {
        if (!order.StopLossPrice.HasValue && !order.TakeProfitPrice.HasValue)
        {
            return detail;
        }

        var combined = FormattableString.Invariant(
            $"{detail}; ProtectiveRule=Stop:{FormatNullableDecimal(order.StopLossPrice)}|Take:{FormatNullableDecimal(order.TakeProfitPrice)}");

        return combined.Length <= 512
            ? combined
            : combined[..512];
    }

    private static string AppendWatchdogDetail(string detail)
    {
        var combined = $"{detail}; ObservedBy=VirtualExecutionWatchdog";

        return combined.Length <= 512
            ? combined
            : combined[..512];
    }

    private Task WriteInternalExecutionTraceAsync(
        ExecutionOrder order,
        string endpoint,
        string requestMasked,
        string responseMasked,
        string executionAttemptId,
        CancellationToken cancellationToken)
    {
        return traceService is null
            ? Task.CompletedTask
            : traceService.WriteExecutionTraceAsync(
                new ExecutionTraceWriteRequest(
                    order.IdempotencyKey,
                    order.OwnerUserId,
                    nameof(DemoFillSimulator),
                    endpoint,
                    Truncate(requestMasked, 4096),
                    Truncate(responseMasked, 4096),
                    order.RootCorrelationId,
                    executionAttemptId,
                    order.Id),
                cancellationToken);
    }

    private static string BuildInternalExecutionTraceRequest(ExecutionOrder order, string phase)
    {
        return FormattableString.Invariant(
            $"Environment={order.ExecutionEnvironment}; Executor={nameof(VirtualExecutor)}; OutboundBrokerRequested=False; SimulatedFillPathUsed=True; TracePersisted=True; Phase={phase}; Symbol={order.Symbol}; Side={order.Side}; OrderType={order.OrderType}; ReduceOnly={order.ReduceOnly}");
    }

    private static string BuildInternalExecutionTraceResponse(ExecutionOrder order, DemoFillSimulation simulation)
    {
        return FormattableString.Invariant(
            $"Environment={order.ExecutionEnvironment}; Executor={nameof(VirtualExecutor)}; OutboundBrokerRequested=False; SimulatedFillPathUsed=True; TracePersisted=True; Phase=WatchdogFill; State={order.State}; EventCode={simulation.EventCode}; FillQuantity={simulation.FillQuantity:0.##################}; FillPrice={simulation.FillPrice:0.##################}; Detail={Truncate(simulation.Detail, 512) ?? "none"}");
    }

    private static string FormatNullableDecimal(decimal? value)
    {
        return value?.ToString("0.##################", CultureInfo.InvariantCulture) ?? "none";
    }

    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Length <= maxLength
            ? value
            : value[..maxLength];
    }

    private static string ResolveFailureCode(Exception exception)
    {
        return exception switch
        {
            ExecutionValidationException validationException => validationException.ReasonCode,
            ExecutionGateRejectedException gateRejectedException => gateRejectedException.Reason.ToString(),
            _ => "VirtualWatchdogFailedClosed"
        };
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

    private static string CreateCorrelationId()
    {
        return Guid.NewGuid().ToString("N");
    }
}




