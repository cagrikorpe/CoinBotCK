using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CoinBot.Application.Abstractions.Administration;
using CoinBot.Application.Abstractions.DemoPortfolio;
using CoinBot.Application.Abstractions.Execution;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Alerts;
using CoinBot.Infrastructure.Dashboard;
using CoinBot.Infrastructure.Observability;
using CoinBot.Infrastructure.Persistence;
using CoinBot.Infrastructure.Exchange;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CoinBot.Infrastructure.Execution;

public sealed class ExecutionEngine(
    ApplicationDbContext dbContext,
    IExecutionGate executionGate,
    ITradingModeResolver tradingModeResolver,
    ITraceService traceService,
    IUserExecutionOverrideGuard userExecutionOverrideGuard,
    ICorrelationContextAccessor correlationContextAccessor,
    IDemoPortfolioAccountingService demoPortfolioAccountingService,
    DemoFillSimulator demoFillSimulator,
    VirtualExecutor virtualExecutor,
    BinanceExecutor binanceExecutor,
    BinanceSpotExecutor binanceSpotExecutor,
    ExecutionOrderLifecycleService executionOrderLifecycleService,
    TimeProvider timeProvider,
    ILogger<ExecutionEngine> logger,
    IAlertDispatchCoordinator? alertDispatchCoordinator = null,
    IHostEnvironment? hostEnvironment = null,
    UserOperationsStreamHub? userOperationsStreamHub = null) : IExecutionEngine
{
    private static readonly ExecutionOrderState[] OpenStates =
    [
        ExecutionOrderState.Received,
        ExecutionOrderState.GatePassed,
        ExecutionOrderState.Dispatching,
        ExecutionOrderState.Submitted,
        ExecutionOrderState.PartiallyFilled,
        ExecutionOrderState.CancelRequested
    ];

    public async Task<ExecutionDispatchResult> DispatchAsync(
        ExecutionCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var normalizedCommand = NormalizeCommand(command);
        var decisionTrace = string.IsNullOrWhiteSpace(normalizedCommand.CorrelationId)
            ? await traceService.GetDecisionTraceByStrategySignalIdAsync(normalizedCommand.StrategySignalId, cancellationToken)
            : null;
        var rootCorrelationId = ResolveRootCorrelationId(normalizedCommand.CorrelationId, decisionTrace?.CorrelationId);
        var idempotencyKey = ResolveIdempotencyKey(normalizedCommand);
        var existingOrderId = await dbContext.ExecutionOrders
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(entity =>
                entity.OwnerUserId == normalizedCommand.OwnerUserId &&
                entity.IdempotencyKey == idempotencyKey &&
                !entity.IsDeleted)
            .Select(entity => entity.Id)
            .SingleOrDefaultAsync(cancellationToken);

        if (existingOrderId != Guid.Empty)
        {
            logger.LogInformation(
                "Execution command duplicate suppressed for OwnerUserId {OwnerUserId}, StrategySignalId {StrategySignalId}.",
                normalizedCommand.OwnerUserId,
                normalizedCommand.StrategySignalId);

            await MarkDuplicateSuppressedAsync(existingOrderId, cancellationToken);

            return new ExecutionDispatchResult(
                await GetSnapshotAsync(existingOrderId, cancellationToken),
                IsDuplicate: true);
        }

        var requestedEnvironment = await ResolveRequestedEnvironmentAsync(normalizedCommand, cancellationToken);
        var executionPlane = ResolveExecutionPlane(requestedEnvironment, normalizedCommand.Plane);
        var executor = ResolveExecutor(requestedEnvironment, executionPlane);
        var utcNow = timeProvider.GetUtcNow().UtcDateTime;
        var order = new ExecutionOrder
        {
            OwnerUserId = normalizedCommand.OwnerUserId,
            TradingStrategyId = normalizedCommand.TradingStrategyId,
            TradingStrategyVersionId = normalizedCommand.TradingStrategyVersionId,
            StrategySignalId = normalizedCommand.StrategySignalId,
            SignalType = normalizedCommand.SignalType,
            BotId = normalizedCommand.BotId,
            ExchangeAccountId = normalizedCommand.ExchangeAccountId,
            Plane = executionPlane,
            StrategyKey = normalizedCommand.StrategyKey,
            Symbol = normalizedCommand.Symbol,
            Timeframe = normalizedCommand.Timeframe,
            BaseAsset = normalizedCommand.BaseAsset,
            QuoteAsset = normalizedCommand.QuoteAsset,
            Side = normalizedCommand.Side,
            OrderType = normalizedCommand.OrderType,
            Quantity = normalizedCommand.Quantity,
            Price = normalizedCommand.Price,
            StopLossPrice = normalizedCommand.StopLossPrice,
            TakeProfitPrice = normalizedCommand.TakeProfitPrice,
            ReduceOnly = normalizedCommand.ReduceOnly,
            ReplacesExecutionOrderId = normalizedCommand.ReplacesExecutionOrderId,
            ExecutionEnvironment = requestedEnvironment,
            ExecutorKind = executor.Kind,
            IdempotencyKey = idempotencyKey,
            RootCorrelationId = rootCorrelationId,
            ParentCorrelationId = NormalizeOptional(normalizedCommand.ParentCorrelationId),
            LastStateChangedAtUtc = utcNow
        };
        var transitions = new List<ExecutionOrderTransition>();

        using var activity = CoinBotActivity.StartActivity("CoinBot.Execution.Dispatch");
        activity.SetTag("coinbot.correlation_id", rootCorrelationId);
        activity.SetTag("coinbot.execution.root_correlation_id", rootCorrelationId);
        activity.SetTag("coinbot.execution.parent_correlation_id", order.ParentCorrelationId ?? "none");
        activity.SetTag("coinbot.execution.environment", requestedEnvironment.ToString());
        activity.SetTag("coinbot.execution.executor", executor.Kind.ToString());
        activity.SetTag("coinbot.execution.signal_id", normalizedCommand.StrategySignalId.ToString());

        dbContext.ExecutionOrders.Add(order);
        var initialTransition = ExecutionOrderStateMachine.CreateInitialTransition(
            order,
            utcNow,
            CreateTransitionCorrelationId(),
            order.ParentCorrelationId ?? rootCorrelationId,
            detail: "Execution command accepted.");
        transitions.Add(initialTransition);
        dbContext.ExecutionOrderTransitions.Add(initialTransition);
        await dbContext.SaveChangesAsync(cancellationToken);

        var lastTransition = initialTransition;
        activity.SetTag("coinbot.execution.order_id", order.Id.ToString());

        try
        {
            await EnsureReplacementOrderEligibleAsync(normalizedCommand, cancellationToken);
            await ValidatePreSubmitOrderAsync(normalizedCommand, requestedEnvironment, cancellationToken);

            await executionGate.EnsureExecutionAllowedAsync(
                new ExecutionGateRequest(
                    Actor: normalizedCommand.Actor,
                    Action: "TradeExecution.Dispatch",
                    Target: $"ExecutionOrder/{order.Id}",
                    Environment: requestedEnvironment,
                    Context: BuildGateContext(
                        normalizedCommand.Context,
                        order.Id,
                        idempotencyKey,
                        normalizedCommand.AdministrativeOverride ? normalizedCommand.AdministrativeOverrideReason : null),
                    CorrelationId: rootCorrelationId,
                    UserId: normalizedCommand.OwnerUserId,
                    BotId: normalizedCommand.BotId,
                    StrategyKey: normalizedCommand.StrategyKey,
                    Symbol: normalizedCommand.Symbol,
                    Timeframe: normalizedCommand.Timeframe,
                    ExchangeAccountId: normalizedCommand.ExchangeAccountId,
                    Plane: executionPlane),
                cancellationToken);

            lastTransition = await PersistTransitionAsync(
                order,
                transitions,
                ExecutionOrderState.GatePassed,
                "GatePassed",
                "Execution gate approved the order.",
                lastTransition.CorrelationId,
                cancellationToken);
            lastTransition = await PersistTransitionAsync(
                order,
                transitions,
                ExecutionOrderState.Dispatching,
                "Dispatching",
                $"Dispatch started via {executor.Kind}.",
                lastTransition.CorrelationId,
                cancellationToken);

            if (!normalizedCommand.AdministrativeOverride)
            {
                var overrideEvaluation = await userExecutionOverrideGuard.EvaluateAsync(
                    new UserExecutionOverrideEvaluationRequest(
                        normalizedCommand.OwnerUserId,
                        normalizedCommand.Symbol,
                        requestedEnvironment,
                        normalizedCommand.Side,
                        normalizedCommand.Quantity,
                        normalizedCommand.Price,
                        normalizedCommand.BotId,
                        normalizedCommand.StrategyKey,
                        normalizedCommand.Context,
                        normalizedCommand.TradingStrategyId,
                        normalizedCommand.TradingStrategyVersionId,
                        normalizedCommand.Timeframe,
                        order.Id,
                        normalizedCommand.ReplacesExecutionOrderId,
                        executionPlane),
                    cancellationToken);

                if (overrideEvaluation.IsBlocked)
                {
                    order.FailureCode = overrideEvaluation.BlockCode;
                    order.FailureDetail = Truncate(overrideEvaluation.Message, 512);
                    ApplyPreSubmitRejectionMetadata(order);

                    lastTransition = await PersistTransitionAsync(
                        order,
                        transitions,
                        ExecutionOrderState.Rejected,
                        "UserExecutionOverrideBlocked",
                        Truncate(overrideEvaluation.Message, 512),
                        lastTransition.CorrelationId,
                        cancellationToken);

                    await TrySendExecutionAlertAsync(order, lastTransition, cancellationToken);
                    userOperationsStreamHub?.Publish(BuildOperationsUpdate(order, lastTransition));

                    logger.LogWarning(
                        "Execution engine rejected order {ExecutionOrderId} because of user execution override {BlockCode}.",
                        order.Id,
                        overrideEvaluation.BlockCode);

                    await UpdateBotOpenOrderCountAsync(order.BotId, cancellationToken);
                    await dbContext.SaveChangesAsync(cancellationToken);

                    return new ExecutionDispatchResult(
                        await GetSnapshotAsync(order.Id, cancellationToken),
                        IsDuplicate: false);
                }
            }

            order.SubmittedToBroker = true;
            order.RejectionStage = ExecutionRejectionStage.None;
            order.RetryEligible = false;
            order.CooldownApplied = false;
            await dbContext.SaveChangesAsync(cancellationToken);

            var dispatchResult = await executor.DispatchAsync(order, normalizedCommand, cancellationToken);
            order.ExternalOrderId = NormalizeOptional(dispatchResult.ExternalOrderId);
            order.SubmittedAtUtc = dispatchResult.SubmittedAtUtc;
            order.CooldownApplied = true;

            lastTransition = await PersistTransitionAsync(
                order,
                transitions,
                ExecutionOrderState.Submitted,
                "Submitted",
                Truncate(dispatchResult.Detail, 512),
                lastTransition.CorrelationId,
                cancellationToken);

            await TrySendExecutionAlertAsync(order, lastTransition, cancellationToken);
            userOperationsStreamHub?.Publish(BuildOperationsUpdate(order, lastTransition));

            if (requestedEnvironment == ExecutionEnvironment.Demo)
            {
                lastTransition = await HandleDemoSubmissionAsync(
                    order,
                    transitions,
                    lastTransition,
                    cancellationToken);
            }
            else
            {
                await ApplyInitialBrokerSnapshotAsync(dispatchResult.InitialSnapshot, cancellationToken);
            }
        }
        catch (ExecutionValidationException exception)
        {
            order.FailureCode = exception.ReasonCode;
            order.FailureDetail = Truncate(exception.Message, 512);
            ApplyPreSubmitRejectionMetadata(order);

            lastTransition = await PersistTransitionAsync(
                order,
                transitions,
                ExecutionOrderState.Rejected,
                "ExecutionValidationRejected",
                Truncate(exception.Message, 512),
                lastTransition.CorrelationId,
                cancellationToken);

            await TrySendExecutionAlertAsync(order, lastTransition, cancellationToken);
            userOperationsStreamHub?.Publish(BuildOperationsUpdate(order, lastTransition));

            logger.LogWarning(
                exception,
                "Execution engine rejected order {ExecutionOrderId} before broker submission with validation reason {ReasonCode}.",
                order.Id,
                exception.ReasonCode);
        }
        catch (ExecutionGateRejectedException exception)
        {
            order.FailureCode = exception.Reason.ToString();
            order.FailureDetail = Truncate(exception.Message, 512);
            ApplyPreSubmitRejectionMetadata(order);

            lastTransition = await PersistTransitionAsync(
                order,
                transitions,
                ExecutionOrderState.Rejected,
                "GateRejected",
                Truncate(exception.Message, 512),
                lastTransition.CorrelationId,
                cancellationToken);

            await TrySendExecutionAlertAsync(order, lastTransition, cancellationToken);
            userOperationsStreamHub?.Publish(BuildOperationsUpdate(order, lastTransition));

            logger.LogWarning(
                "Execution engine rejected order {ExecutionOrderId} with reason {Reason}.",
                order.Id,
                exception.Reason);
        }
        catch (Exception exception)
        {
            order.FailureCode = ResolveFailureCode(exception, order.SubmittedToBroker);
            order.FailureDetail = Truncate(exception.Message, 512);
            var transitionState = order.SubmittedToBroker
                ? ExecutionOrderState.Failed
                : ExecutionOrderState.Rejected;
            var eventCode = order.SubmittedToBroker
                ? "DispatchFailed"
                : "PreSubmitFailed";

            if (order.SubmittedToBroker)
            {
                ApplyPostSubmitFailureMetadata(order);
            }
            else
            {
                ApplyPreSubmitRejectionMetadata(order);
            }

            lastTransition = await PersistTransitionAsync(
                order,
                transitions,
                transitionState,
                eventCode,
                Truncate(exception.Message, 512),
                lastTransition.CorrelationId,
                cancellationToken);

            await TrySendExecutionAlertAsync(order, lastTransition, cancellationToken);
            userOperationsStreamHub?.Publish(BuildOperationsUpdate(order, lastTransition));

            logger.LogWarning(
                exception,
                "Execution engine failed closed for order {ExecutionOrderId}.",
                order.Id);
        }

        await UpdateBotOpenOrderCountAsync(order.BotId, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);

        return new ExecutionDispatchResult(
            await GetSnapshotAsync(order.Id, cancellationToken),
            IsDuplicate: false);
    }

    private async Task<ExecutionOrderTransition> HandleDemoSubmissionAsync(
        ExecutionOrder order,
        IList<ExecutionOrderTransition> transitions,
        ExecutionOrderTransition lastTransition,
        CancellationToken cancellationToken)
    {
        var simulation = await demoFillSimulator.SimulateOnSubmissionAsync(order, cancellationToken);

        if (simulation.Reservation is not null)
        {
            await demoPortfolioAccountingService.ReserveFundsAsync(
                new DemoFundsReservationRequest(
                    order.OwnerUserId,
                    ExecutionEnvironment.Demo,
                    BuildDemoOperationId("reserve", order.Id),
                    simulation.Reservation.Asset,
                    simulation.Reservation.Amount,
                    BuildDemoOrderId(order.Id),
                    order.SubmittedAtUtc ?? timeProvider.GetUtcNow().UtcDateTime),
                cancellationToken);
        }

        if (simulation.Fill is null)
        {
            return lastTransition;
        }

        try
        {
            var fill = simulation.Fill;
            var fillDetail = AppendProtectiveRule(fill.Detail, order);
            await demoPortfolioAccountingService.ApplyFillAsync(
                new DemoFillAccountingRequest(
                    order.OwnerUserId,
                    ExecutionEnvironment.Demo,
                    BuildDemoOperationId("fill", order.Id, transitions.Count + 1),
                    order.Symbol,
                    order.BaseAsset,
                    order.QuoteAsset,
                    MapTradeSide(order.Side),
                    fill.FillQuantity,
                    fill.FillPrice,
                    simulation.Reservation?.ConsumedAmount ?? 0m,
                    order.BotId,
                    BuildDemoOrderId(order.Id),
                    BuildDemoFillId(order.Id, transitions.Count + 1),
                    fill.FeeAsset,
                    fill.FeeAmount,
                    fill.FeeAmountInQuote,
                    MarkPrice: ResolveDemoMarkPrice(fill),
                    OccurredAtUtc: fill.ObservedAtUtc),
                cancellationToken);

            var totalFilledQuantity = order.FilledQuantity + fill.FillQuantity;
            order.AverageFillPrice = BlendAverageFillPrice(
                order.AverageFillPrice,
                order.FilledQuantity,
                fill.FillPrice,
                fill.FillQuantity);
            order.FilledQuantity = totalFilledQuantity;
            order.LastFilledAtUtc = fill.ObservedAtUtc;

            var transition = await PersistTransitionAsync(
                order,
                transitions,
                fill.IsFinalFill
                    ? ExecutionOrderState.Filled
                    : ExecutionOrderState.PartiallyFilled,
                fill.EventCode,
                fillDetail,
                lastTransition.CorrelationId,
                cancellationToken);

            await TrySendExecutionAlertAsync(order, transition, cancellationToken);
            userOperationsStreamHub?.Publish(BuildOperationsUpdate(order, transition));
            return transition;
        }
        catch (Exception exception)
        {
            if (simulation.Reservation is not null)
            {
                await TryReleaseDemoReservationAsync(order, simulation.Reservation, cancellationToken);
            }

            order.FailureCode = ResolveFailureCode(exception, submittedToBroker: true, fallbackFailureCode: "DemoSimulationFailed");
            order.FailureDetail = Truncate(exception.Message, 512);
            ApplyPostSubmitFailureMetadata(order);

            logger.LogWarning(
                exception,
                "Demo fill simulation failed closed for order {ExecutionOrderId}.",
                order.Id);

            var transition = await PersistTransitionAsync(
                order,
                transitions,
                ExecutionOrderState.Failed,
                "DemoSimulationFailed",
                Truncate(exception.Message, 512),
                lastTransition.CorrelationId,
                cancellationToken);

            await TrySendExecutionAlertAsync(order, transition, cancellationToken);
            userOperationsStreamHub?.Publish(BuildOperationsUpdate(order, transition));
            return transition;
        }
    }

    private async Task TryReleaseDemoReservationAsync(
        ExecutionOrder order,
        DemoFillReservationPlan reservation,
        CancellationToken cancellationToken)
    {
        try
        {
            await demoPortfolioAccountingService.ReleaseFundsAsync(
                new DemoFundsReleaseRequest(
                    order.OwnerUserId,
                    ExecutionEnvironment.Demo,
                    BuildDemoOperationId("release-failed", order.Id),
                    reservation.Asset,
                    reservation.Amount,
                    BuildDemoOrderId(order.Id),
                    order.SubmittedAtUtc ?? timeProvider.GetUtcNow().UtcDateTime),
                cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Demo reservation release fallback failed for order {ExecutionOrderId}.",
                order.Id);
        }
    }

    private async Task<ExecutionOrderTransition> PersistTransitionAsync(
        ExecutionOrder order,
        IList<ExecutionOrderTransition> transitions,
        ExecutionOrderState targetState,
        string eventCode,
        string? detail,
        string? parentCorrelationId,
        CancellationToken cancellationToken)
    {
        var transition = ExecutionOrderStateMachine.Transition(
            order,
            sequenceNumber: transitions.Count + 1,
            targetState,
            eventCode,
            timeProvider.GetUtcNow().UtcDateTime,
            CreateTransitionCorrelationId(),
            parentCorrelationId,
            detail);

        transitions.Add(transition);
        dbContext.ExecutionOrderTransitions.Add(transition);
        await dbContext.SaveChangesAsync(cancellationToken);

        return transition;
    }

    private async Task<ExecutionOrderSnapshot> GetSnapshotAsync(
        Guid executionOrderId,
        CancellationToken cancellationToken)
    {
        var order = await dbContext.ExecutionOrders
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(
                entity => entity.Id == executionOrderId &&
                          !entity.IsDeleted,
                cancellationToken);
        var transitions = await dbContext.ExecutionOrderTransitions
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(entity =>
                entity.ExecutionOrderId == executionOrderId &&
                !entity.IsDeleted)
            .OrderBy(entity => entity.SequenceNumber)
            .ToListAsync(cancellationToken);

        return new ExecutionOrderSnapshot(
            order.Id,
            order.TradingStrategyId,
            order.TradingStrategyVersionId,
            order.StrategySignalId,
            order.SignalType,
            order.BotId,
            order.ExchangeAccountId,
            order.StrategyKey,
            order.Symbol,
            order.Timeframe,
            order.BaseAsset,
            order.QuoteAsset,
            order.Side,
            order.OrderType,
            order.Quantity,
            order.Price,
            order.FilledQuantity,
            order.AverageFillPrice,
            order.LastFilledAtUtc,
            order.StopLossPrice,
            order.TakeProfitPrice,
            order.ReduceOnly,
            order.ReplacesExecutionOrderId,
            order.ExecutionEnvironment,
            order.ExecutorKind,
            order.State,
            order.IdempotencyKey,
            order.RootCorrelationId,
            order.ParentCorrelationId,
            order.ExternalOrderId,
            order.FailureCode,
            order.FailureDetail,
            order.RejectionStage,
            order.SubmittedToBroker,
            order.RetryEligible,
            order.CooldownApplied,
            order.DuplicateSuppressed,
            order.StopLossPrice.HasValue,
            order.TakeProfitPrice.HasValue,
            ResolveClientOrderId(order, transitions),
            order.SubmittedAtUtc,
            order.LastReconciledAtUtc,
            order.ReconciliationStatus,
            order.ReconciliationSummary,
            order.LastDriftDetectedAtUtc,
            order.LastStateChangedAtUtc,
            transitions
                .Select(transition => new ExecutionOrderTransitionSnapshot(
                    transition.Id,
                    transition.SequenceNumber,
                    transition.State,
                    transition.EventCode,
                    transition.Detail,
                    transition.CorrelationId,
                    transition.ParentCorrelationId,
                    transition.OccurredAtUtc))
                .ToArray());
    }

    private async Task ApplyInitialBrokerSnapshotAsync(
        BinanceOrderStatusSnapshot? initialSnapshot,
        CancellationToken cancellationToken)
    {
        if (initialSnapshot is null)
        {
            return;
        }

        await executionOrderLifecycleService.ApplyExchangeUpdateAsync(initialSnapshot, cancellationToken);
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
                          OpenStates.Contains(entity.State),
                cancellationToken);
    }

    private async Task EnsureReplacementOrderEligibleAsync(
        ExecutionCommand command,
        CancellationToken cancellationToken)
    {
        if (!command.ReplacesExecutionOrderId.HasValue)
        {
            return;
        }

        var existingOrder = await dbContext.ExecutionOrders
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleOrDefaultAsync(
                entity => entity.Id == command.ReplacesExecutionOrderId.Value &&
                          entity.OwnerUserId == command.OwnerUserId &&
                          !entity.IsDeleted,
                cancellationToken)
            ?? throw new ExecutionValidationException(
                "ReplacementSourceOrderNotFound",
                $"Execution blocked because replacement source order '{command.ReplacesExecutionOrderId.Value}' was not found.");

        if (!OpenStates.Contains(existingOrder.State))
        {
            throw new ExecutionValidationException(
                "ReplacementSourceOrderClosed",
                "Execution blocked because the replacement source order is not open.");
        }

        if (!string.Equals(existingOrder.Symbol, command.Symbol, StringComparison.Ordinal) ||
            existingOrder.Side != command.Side)
        {
            throw new ExecutionValidationException(
                "ReplacementSourceOrderScopeMismatch",
                "Execution blocked because the replacement source order does not match symbol and side.");
        }

        if (existingOrder.ExchangeAccountId != command.ExchangeAccountId)
        {
            throw new ExecutionValidationException(
                "ReplacementSourceOrderExchangeMismatch",
                "Execution blocked because the replacement source order does not match the exchange account scope.");
        }
    }

    private async Task ValidatePreSubmitOrderAsync(
        ExecutionCommand command,
        ExecutionEnvironment requestedEnvironment,
        CancellationToken cancellationToken)
    {
        ValidateProtectiveTargets(command);
        await ValidateReduceOnlyOrderAsync(command, requestedEnvironment, command.Plane, cancellationToken);
    }

    private async Task ValidateReduceOnlyOrderAsync(
        ExecutionCommand command,
        ExecutionEnvironment requestedEnvironment,
        ExchangeDataPlane plane,
        CancellationToken cancellationToken)
    {
        if (!command.ReduceOnly)
        {
            return;
        }

        if (requestedEnvironment == ExecutionEnvironment.Live &&
            plane == ExchangeDataPlane.Spot)
        {
            throw new ExecutionValidationException(
                "SpotReduceOnlyUnsupported",
                $"Execution blocked because reduce-only is not supported for spot order flow on {command.Symbol}.");
        }

        var netQuantity = await ResolveNetPositionQuantityAsync(
            command.OwnerUserId,
            command.Symbol,
            requestedEnvironment,
            plane,
            cancellationToken);

        if (netQuantity == 0m)
        {
            throw new ExecutionValidationException(
                "ReduceOnlyWithoutOpenPosition",
                $"Execution blocked because reduce-only order requires an open position for {command.Symbol}.");
        }

        var expectedSide = netQuantity > 0m
            ? ExecutionOrderSide.Sell
            : ExecutionOrderSide.Buy;

        if (command.Side != expectedSide)
        {
            throw new ExecutionValidationException(
                "ReduceOnlyWouldIncreaseExposure",
                $"Execution blocked because reduce-only {command.Side} would increase exposure for {command.Symbol}.");
        }

        var openQuantity = Math.Abs(netQuantity);
        if (command.Quantity > openQuantity)
        {
            throw new ExecutionValidationException(
                "ReduceOnlyQuantityExceedsOpenPosition",
                $"Execution blocked because reduce-only quantity {command.Quantity.ToString("0.##################", CultureInfo.InvariantCulture)} exceeds open position {openQuantity.ToString("0.##################", CultureInfo.InvariantCulture)} for {command.Symbol}.");
        }
    }

    private async Task<decimal> ResolveNetPositionQuantityAsync(
        string ownerUserId,
        string symbol,
        ExecutionEnvironment requestedEnvironment,
        ExchangeDataPlane plane,
        CancellationToken cancellationToken)
    {
        return requestedEnvironment == ExecutionEnvironment.Demo
            ? await dbContext.DemoPositions
                .AsNoTracking()
                .IgnoreQueryFilters()
                .Where(entity =>
                    entity.OwnerUserId == ownerUserId &&
                    entity.Symbol == symbol &&
                    !entity.IsDeleted)
                .SumAsync(entity => entity.Quantity, cancellationToken)
            : await dbContext.ExchangePositions
                .AsNoTracking()
                .IgnoreQueryFilters()
                .Where(entity =>
                    entity.OwnerUserId == ownerUserId &&
                    entity.Plane == plane &&
                    entity.Symbol == symbol &&
                    !entity.IsDeleted)
                .SumAsync(entity => entity.Quantity, cancellationToken);
    }

    private async Task MarkDuplicateSuppressedAsync(
        Guid executionOrderId,
        CancellationToken cancellationToken)
    {
        var existingOrder = await dbContext.ExecutionOrders
            .IgnoreQueryFilters()
            .SingleAsync(
                entity => entity.Id == executionOrderId &&
                          !entity.IsDeleted,
                cancellationToken);

        existingOrder.DuplicateSuppressed = true;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static void ApplyPreSubmitRejectionMetadata(ExecutionOrder order)
    {
        order.SubmittedToBroker = false;
        order.RejectionStage = ExecutionRejectionStage.PreSubmit;
        order.RetryEligible = false;
        order.CooldownApplied = false;
    }

    private static void ApplyPostSubmitFailureMetadata(ExecutionOrder order)
    {
        order.SubmittedToBroker = true;
        order.RejectionStage = ExecutionRejectionStage.PostSubmit;
        order.RetryEligible = true;
        order.CooldownApplied = true;
    }

    private static string ResolveFailureCode(Exception exception, bool submittedToBroker, string? fallbackFailureCode = null)
    {
        return exception switch
        {
            ExecutionValidationException validationException => validationException.ReasonCode,
            ExecutionGateRejectedException gateRejectedException => gateRejectedException.Reason.ToString(),
            BinanceClockDriftException => nameof(ExecutionGateBlockedReason.ClockDriftExceeded),
            _ when !string.IsNullOrWhiteSpace(fallbackFailureCode) => fallbackFailureCode!,
            _ => submittedToBroker ? "DispatchFailed" : "PreSubmitFailed"
        };
    }
    private IExecutionTargetExecutor ResolveExecutor(
        ExecutionEnvironment requestedEnvironment,
        ExchangeDataPlane plane)
    {
        return requestedEnvironment == ExecutionEnvironment.Demo
            ? virtualExecutor
            : plane == ExchangeDataPlane.Spot
                ? binanceSpotExecutor
                : binanceExecutor;
    }

    private static DemoTradeSide MapTradeSide(ExecutionOrderSide side)
    {
        return side == ExecutionOrderSide.Buy
            ? DemoTradeSide.Buy
            : DemoTradeSide.Sell;
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

    private static string BuildDemoOrderId(Guid orderId)
    {
        return orderId.ToString("N");
    }

    private static string BuildDemoFillId(Guid orderId, int sequenceNumber)
    {
        return $"demo-fill:{orderId:N}:{sequenceNumber}";
    }

    private static string BuildDemoOperationId(string phase, Guid orderId, int? sequenceNumber = null)
    {
        return sequenceNumber.HasValue
            ? $"execution-{phase}:{orderId:N}:{sequenceNumber.Value}"
            : $"execution-{phase}:{orderId:N}";
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

    private async Task<ExecutionEnvironment> ResolveRequestedEnvironmentAsync(
        ExecutionCommand command,
        CancellationToken cancellationToken)
    {
        if (command.IsDemo.HasValue)
        {
            return command.IsDemo.Value
                ? ExecutionEnvironment.Demo
                : ExecutionEnvironment.Live;
        }

        var resolution = await tradingModeResolver.ResolveAsync(
            new TradingModeResolutionRequest(
                command.OwnerUserId,
                command.BotId,
                command.StrategyKey),
            cancellationToken);

        return resolution.EffectiveMode;
    }

    private string ResolveRootCorrelationId(string? correlationId, string? fallbackCorrelationId = null)
    {
        var normalizedCorrelationId = NormalizeOptional(correlationId);

        if (!string.IsNullOrWhiteSpace(normalizedCorrelationId))
        {
            return normalizedCorrelationId;
        }

        var normalizedFallbackCorrelationId = NormalizeOptional(fallbackCorrelationId);

        if (!string.IsNullOrWhiteSpace(normalizedFallbackCorrelationId))
        {
            return normalizedFallbackCorrelationId;
        }

        var scopedCorrelationId = correlationContextAccessor.Current?.CorrelationId;

        if (!string.IsNullOrWhiteSpace(scopedCorrelationId))
        {
            return scopedCorrelationId;
        }

        return Guid.NewGuid().ToString("N");
    }

    private static ExecutionCommand NormalizeCommand(ExecutionCommand command)
    {
        var normalizedCommand = command with
        {
            Actor = NormalizeRequired(command.Actor, nameof(command.Actor), 256),
            OwnerUserId = NormalizeRequired(command.OwnerUserId, nameof(command.OwnerUserId), 450),
            StrategyKey = NormalizeRequired(command.StrategyKey, nameof(command.StrategyKey), 128),
            Symbol = NormalizeCode(command.Symbol, nameof(command.Symbol), 32),
            Timeframe = NormalizeRequired(command.Timeframe, nameof(command.Timeframe), 16),
            BaseAsset = NormalizeCode(command.BaseAsset, nameof(command.BaseAsset), 32),
            QuoteAsset = NormalizeCode(command.QuoteAsset, nameof(command.QuoteAsset), 32),
            Quantity = ValidatePositive(command.Quantity, nameof(command.Quantity)),
            Price = ValidatePositive(command.Price, nameof(command.Price)),
            IdempotencyKey = NormalizeOptional(command.IdempotencyKey),
            CorrelationId = NormalizeOptional(command.CorrelationId),
            ParentCorrelationId = NormalizeOptional(command.ParentCorrelationId),
            Context = NormalizeOptional(command.Context),
            QuoteQuantity = ValidateOptionalPositive(command.QuoteQuantity, nameof(command.QuoteQuantity)),
            TimeInForce = NormalizeOptional(command.TimeInForce)?.ToUpperInvariant(),
            StopLossPrice = ValidateOptionalPositive(command.StopLossPrice, nameof(command.StopLossPrice)),
            TakeProfitPrice = ValidateOptionalPositive(command.TakeProfitPrice, nameof(command.TakeProfitPrice)),
            AdministrativeOverrideReason = NormalizeOptional(command.AdministrativeOverrideReason)
        };

        ValidateAdministrativeOverride(normalizedCommand);
        return normalizedCommand;
    }

    private static string ResolveIdempotencyKey(ExecutionCommand command)
    {
        var explicitKey = NormalizeOptional(command.IdempotencyKey);

        if (!string.IsNullOrWhiteSpace(explicitKey))
        {
            return explicitKey.Length <= 128
                ? explicitKey
                : throw new ArgumentOutOfRangeException(nameof(command.IdempotencyKey), "IdempotencyKey cannot exceed 128 characters.");
        }

        var payload = string.Join(
            "|",
            command.OwnerUserId,
            command.TradingStrategyId,
            command.TradingStrategyVersionId,
            command.StrategySignalId,
            command.SignalType,
            command.StrategyKey,
            command.Symbol,
            command.Timeframe,
            command.BaseAsset,
            command.QuoteAsset,
            command.Side,
            command.OrderType,
            command.Quantity.ToString("0.##################", CultureInfo.InvariantCulture),
            command.Price.ToString("0.##################", CultureInfo.InvariantCulture),
            command.Plane.ToString(),
            command.QuoteQuantity?.ToString("0.##################", CultureInfo.InvariantCulture) ?? "none",
            command.TimeInForce ?? "default",
            command.StopLossPrice?.ToString("0.##################", CultureInfo.InvariantCulture) ?? "none",
            command.TakeProfitPrice?.ToString("0.##################", CultureInfo.InvariantCulture) ?? "none",
            command.ReduceOnly ? "true" : "false",
            command.BotId?.ToString("N") ?? "none",
            command.ExchangeAccountId?.ToString("N") ?? "none",
            command.IsDemo?.ToString() ?? "auto",
            command.ReplacesExecutionOrderId?.ToString("N") ?? "none");
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));

        return $"exec_{Convert.ToHexStringLower(hash)[..48]}";
    }

    private static string BuildGateContext(string? requestContext, Guid orderId, string idempotencyKey, string? administrativeOverrideReason = null)
    {
        var contextParts = new List<string>();

        if (!string.IsNullOrWhiteSpace(requestContext))
        {
            contextParts.Add(requestContext);
        }

        contextParts.Add($"ExecutionOrderId={orderId}");
        contextParts.Add($"IdempotencyKey={ShortHash(idempotencyKey)}");

        if (!string.IsNullOrWhiteSpace(administrativeOverrideReason))
        {
            contextParts.Add($"AdministrativeOverride={administrativeOverrideReason}");
        }

        return string.Join(" | ", contextParts);
    }

    private static ExchangeDataPlane ResolveExecutionPlane(
        ExecutionEnvironment requestedEnvironment,
        ExchangeDataPlane requestedPlane)
    {
        return requestedEnvironment == ExecutionEnvironment.Demo
            ? requestedPlane
            : requestedPlane;
    }

    private static void ValidateAdministrativeOverride(ExecutionCommand command)
    {
        if (!command.AdministrativeOverride)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(command.AdministrativeOverrideReason))
        {
            throw new InvalidOperationException("Administrative override requires a reason.");
        }

        if (!command.Actor.StartsWith("admin:", StringComparison.OrdinalIgnoreCase) &&
            !command.Actor.StartsWith("system:", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Administrative override can only be used by administrative actors.");
        }
    }

    private static string CreateTransitionCorrelationId()
    {
        return Guid.NewGuid().ToString("N");
    }

    private static string NormalizeRequired(string? value, string parameterName, int maxLength)
    {
        var normalizedValue = NormalizeOptional(value);

        if (string.IsNullOrWhiteSpace(normalizedValue))
        {
            throw new ArgumentException("The value is required.", parameterName);
        }

        return normalizedValue.Length <= maxLength
            ? normalizedValue
            : throw new ArgumentOutOfRangeException(parameterName, $"The value cannot exceed {maxLength} characters.");
    }

    private static string NormalizeCode(string? value, string parameterName, int maxLength)
    {
        var normalizedValue = NormalizeRequired(value, parameterName, maxLength).ToUpperInvariant();

        return normalizedValue.Length <= maxLength
            ? normalizedValue
            : throw new ArgumentOutOfRangeException(parameterName, $"The value cannot exceed {maxLength} characters.");
    }

    private static decimal ValidatePositive(decimal value, string parameterName)
    {
        return value > 0m
            ? value
            : throw new ArgumentOutOfRangeException(parameterName, "The value must be greater than zero.");
    }

    private static decimal? ValidateOptionalPositive(decimal? value, string parameterName)
    {
        return value.HasValue
            ? ValidatePositive(value.Value, parameterName)
            : null;
    }

    private static void ValidateProtectiveTargets(ExecutionCommand command)
    {
        if (!command.StopLossPrice.HasValue && !command.TakeProfitPrice.HasValue)
        {
            return;
        }

        var isBuy = command.Side == ExecutionOrderSide.Buy;

        if (command.StopLossPrice.HasValue &&
            command.TakeProfitPrice.HasValue)
        {
            var hasMatchingProtectiveSide = isBuy
                ? command.StopLossPrice.Value < command.TakeProfitPrice.Value
                : command.StopLossPrice.Value > command.TakeProfitPrice.Value;

            if (!hasMatchingProtectiveSide)
            {
                throw new ExecutionValidationException(
                    "ProtectiveOrderSideMismatch",
                    "Execution blocked because stop-loss and take-profit targets are not aligned with the execution side.");
            }
        }

        if (command.StopLossPrice.HasValue)
        {
            var isValidStop = isBuy
                ? command.StopLossPrice.Value < command.Price
                : command.StopLossPrice.Value > command.Price;

            if (!isValidStop)
            {
                throw new ExecutionValidationException(
                    "InvalidStopLossConfiguration",
                    "Execution blocked because StopLossPrice must be on the protective side of the entry price.");
            }
        }

        if (command.TakeProfitPrice.HasValue)
        {
            var isValidTakeProfit = isBuy
                ? command.TakeProfitPrice.Value > command.Price
                : command.TakeProfitPrice.Value < command.Price;

            if (!isValidTakeProfit)
            {
                throw new ExecutionValidationException(
                    "InvalidTakeProfitConfiguration",
                    "Execution blocked because TakeProfitPrice must be on the favorable side of the entry price.");
            }
        }
    }

    private string? ResolveClientOrderId(
        ExecutionOrder order,
        IReadOnlyCollection<ExecutionOrderTransition> transitions)
    {
        foreach (var transition in transitions.OrderByDescending(item => item.SequenceNumber))
        {
            var clientOrderId = ExtractClientOrderId(transition.Detail);
            if (!string.IsNullOrWhiteSpace(clientOrderId))
            {
                return Truncate(clientOrderId, 128);
            }
        }

        if (!string.IsNullOrWhiteSpace(order.ExternalOrderId) &&
            order.ExecutionEnvironment == ExecutionEnvironment.Demo)
        {
            return Truncate(order.ExternalOrderId, 128);
        }

        if (order.SubmittedToBroker &&
            order.ExecutionEnvironment == ExecutionEnvironment.Live)
        {
            return hostEnvironment?.IsDevelopment() == true &&
                order.Plane == ExchangeDataPlane.Futures
                ? ExecutionClientOrderId.CreateDevelopmentFuturesPilot(order.Id)
                : ExecutionClientOrderId.Create(order.Id);
        }

        return null;
    }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim();
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

    private static string ShortHash(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexStringLower(hash)[..12];
    }

    private async Task TrySendExecutionAlertAsync(
        ExecutionOrder order,
        ExecutionOrderTransition transition,
        CancellationToken cancellationToken)
    {
        if (alertDispatchCoordinator is null)
        {
            return;
        }

        var eventType = transition.State switch
        {
            ExecutionOrderState.Submitted => "OrderSubmitted",
            ExecutionOrderState.Filled => "OrderFilled",
            ExecutionOrderState.Rejected => "OrderRejected",
            ExecutionOrderState.Failed => "OrderFailed",
            _ => null
        };

        if (eventType is null)
        {
            return;
        }

        var botLabel = order.BotId.HasValue
            ? order.BotId.Value.ToString("N")[..12]
            : "none";
        var clientOrderId = ExtractClientOrderId(transition.Detail);
        var severity = transition.State switch
        {
            ExecutionOrderState.Submitted => CoinBot.Application.Abstractions.Alerts.AlertSeverity.Information,
            ExecutionOrderState.Filled => CoinBot.Application.Abstractions.Alerts.AlertSeverity.Information,
            ExecutionOrderState.Rejected => CoinBot.Application.Abstractions.Alerts.AlertSeverity.Warning,
            _ => CoinBot.Application.Abstractions.Alerts.AlertSeverity.Critical
        };

        await alertDispatchCoordinator.SendAsync(
            new CoinBot.Application.Abstractions.Alerts.AlertNotification(
                Code: $"ORDER_{transition.State.ToString().ToUpperInvariant()}",
                Severity: severity,
                Title: eventType,
                Message:
                    $"EventType={eventType}; BotId={botLabel}; Symbol={order.Symbol}; Result={transition.State}; FailureCode={order.FailureCode ?? "none"}; ClientOrderId={clientOrderId ?? "none"}; TimestampUtc={transition.OccurredAtUtc:O}; Environment={ResolveExecutionEnvironmentLabel(order.ExecutionEnvironment)}",
                CorrelationId: transition.CorrelationId),
            $"order-transition:{transition.Id:N}",
            TimeSpan.FromDays(7),
            cancellationToken);
    }

    private string ResolveExecutionEnvironmentLabel(ExecutionEnvironment executionEnvironment)
    {
        var runtimeLabel = hostEnvironment?.EnvironmentName ?? "Unknown";
        var executionLabel = hostEnvironment?.IsDevelopment() == true && executionEnvironment == ExecutionEnvironment.Live
            ? "Testnet"
            : executionEnvironment.ToString();

        return $"{runtimeLabel}/{executionLabel}";
    }

    private static string? ExtractClientOrderId(string? detail)
    {
        const string prefix = "ClientOrderId=";

        if (string.IsNullOrWhiteSpace(detail))
        {
            return null;
        }

        foreach (var segment in detail.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (segment.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return segment[prefix.Length..].Trim();
            }
        }

        return null;
    }

    private static UserOperationsUpdate BuildOperationsUpdate(
        ExecutionOrder order,
        ExecutionOrderTransition transition)
    {
        return new UserOperationsUpdate(
            order.OwnerUserId,
            "ExecutionChanged",
            order.BotId,
            order.Id,
            transition.State.ToString(),
            order.FailureCode,
            transition.OccurredAtUtc);
    }
}

