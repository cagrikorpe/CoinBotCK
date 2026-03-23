using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CoinBot.Application.Abstractions.DemoPortfolio;
using CoinBot.Application.Abstractions.Execution;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Observability;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CoinBot.Infrastructure.Execution;

public sealed class ExecutionEngine(
    ApplicationDbContext dbContext,
    IExecutionGate executionGate,
    ITradingModeResolver tradingModeResolver,
    ICorrelationContextAccessor correlationContextAccessor,
    IDemoPortfolioAccountingService demoPortfolioAccountingService,
    DemoFillSimulator demoFillSimulator,
    VirtualExecutor virtualExecutor,
    BinanceExecutor binanceExecutor,
    TimeProvider timeProvider,
    ILogger<ExecutionEngine> logger) : IExecutionEngine
{
    private static readonly IReadOnlySet<ExecutionOrderState> OpenStates = new HashSet<ExecutionOrderState>
    {
        ExecutionOrderState.Received,
        ExecutionOrderState.GatePassed,
        ExecutionOrderState.Dispatching,
        ExecutionOrderState.Submitted,
        ExecutionOrderState.PartiallyFilled
    };

    public async Task<ExecutionDispatchResult> DispatchAsync(
        ExecutionCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var normalizedCommand = NormalizeCommand(command);
        var rootCorrelationId = ResolveRootCorrelationId(normalizedCommand.CorrelationId);
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

            return new ExecutionDispatchResult(
                await GetSnapshotAsync(existingOrderId, cancellationToken),
                IsDuplicate: true);
        }

        await EnsureReplacementOrderEligibleAsync(normalizedCommand, cancellationToken);
        var requestedEnvironment = await ResolveRequestedEnvironmentAsync(normalizedCommand, cancellationToken);
        var executor = ResolveExecutor(requestedEnvironment);
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
            await executionGate.EnsureExecutionAllowedAsync(
                new ExecutionGateRequest(
                    Actor: normalizedCommand.Actor,
                    Action: "TradeExecution.Dispatch",
                    Target: $"ExecutionOrder/{order.Id}",
                    Environment: requestedEnvironment,
                    Context: BuildGateContext(normalizedCommand.Context, order.Id, idempotencyKey),
                    CorrelationId: rootCorrelationId,
                    UserId: normalizedCommand.OwnerUserId,
                    BotId: normalizedCommand.BotId,
                    StrategyKey: normalizedCommand.StrategyKey),
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

            var dispatchResult = await executor.DispatchAsync(order, normalizedCommand, cancellationToken);
            order.ExternalOrderId = NormalizeOptional(dispatchResult.ExternalOrderId);
            order.SubmittedAtUtc = dispatchResult.SubmittedAtUtc;

            lastTransition = await PersistTransitionAsync(
                order,
                transitions,
                ExecutionOrderState.Submitted,
                "Submitted",
                Truncate(dispatchResult.Detail, 512),
                lastTransition.CorrelationId,
                cancellationToken);

            if (requestedEnvironment == ExecutionEnvironment.Demo)
            {
                lastTransition = await HandleDemoSubmissionAsync(
                    order,
                    transitions,
                    lastTransition,
                    cancellationToken);
            }
        }
        catch (ExecutionGateRejectedException exception)
        {
            order.FailureCode = exception.Reason.ToString();
            order.FailureDetail = Truncate(exception.Message, 512);

            await PersistTransitionAsync(
                order,
                transitions,
                ExecutionOrderState.Rejected,
                "GateRejected",
                Truncate(exception.Message, 512),
                lastTransition.CorrelationId,
                cancellationToken);

            logger.LogWarning(
                "Execution engine rejected order {ExecutionOrderId} with reason {Reason}.",
                order.Id,
                exception.Reason);
        }
        catch (Exception exception)
        {
            order.FailureCode = exception.GetType().Name;
            order.FailureDetail = Truncate(exception.Message, 512);

            await PersistTransitionAsync(
                order,
                transitions,
                ExecutionOrderState.Failed,
                "DispatchFailed",
                Truncate(exception.Message, 512),
                lastTransition.CorrelationId,
                cancellationToken);

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

            return await PersistTransitionAsync(
                order,
                transitions,
                fill.IsFinalFill
                    ? ExecutionOrderState.Filled
                    : ExecutionOrderState.PartiallyFilled,
                fill.EventCode,
                fillDetail,
                lastTransition.CorrelationId,
                cancellationToken);
        }
        catch (Exception exception)
        {
            if (simulation.Reservation is not null)
            {
                await TryReleaseDemoReservationAsync(order, simulation.Reservation, cancellationToken);
            }

            order.FailureCode = exception.GetType().Name;
            order.FailureDetail = Truncate(exception.Message, 512);

            logger.LogWarning(
                exception,
                "Demo fill simulation failed closed for order {ExecutionOrderId}.",
                order.Id);

            return await PersistTransitionAsync(
                order,
                transitions,
                ExecutionOrderState.Failed,
                "DemoSimulationFailed",
                Truncate(exception.Message, 512),
                lastTransition.CorrelationId,
                cancellationToken);
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
            ?? throw new InvalidOperationException(
                $"Replacement source execution order '{command.ReplacesExecutionOrderId.Value}' was not found.");

        if (!OpenStates.Contains(existingOrder.State))
        {
            throw new InvalidOperationException("Replacement source execution order is not open.");
        }

        if (!string.Equals(existingOrder.Symbol, command.Symbol, StringComparison.Ordinal) ||
            existingOrder.Side != command.Side)
        {
            throw new InvalidOperationException("Replacement source execution order does not match symbol and side.");
        }

        if (existingOrder.ExchangeAccountId != command.ExchangeAccountId)
        {
            throw new InvalidOperationException("Replacement source execution order does not match the exchange account scope.");
        }
    }

    private IExecutionTargetExecutor ResolveExecutor(ExecutionEnvironment requestedEnvironment)
    {
        return requestedEnvironment == ExecutionEnvironment.Demo
            ? virtualExecutor
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

    private string ResolveRootCorrelationId(string? correlationId)
    {
        var normalizedCorrelationId = NormalizeOptional(correlationId);

        if (!string.IsNullOrWhiteSpace(normalizedCorrelationId))
        {
            return normalizedCorrelationId;
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
            StopLossPrice = ValidateOptionalPositive(command.StopLossPrice, nameof(command.StopLossPrice)),
            TakeProfitPrice = ValidateOptionalPositive(command.TakeProfitPrice, nameof(command.TakeProfitPrice))
        };

        ValidateProtectiveTargets(normalizedCommand);
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
            command.StopLossPrice?.ToString("0.##################", CultureInfo.InvariantCulture) ?? "none",
            command.TakeProfitPrice?.ToString("0.##################", CultureInfo.InvariantCulture) ?? "none",
            command.BotId?.ToString("N") ?? "none",
            command.ExchangeAccountId?.ToString("N") ?? "none",
            command.IsDemo?.ToString() ?? "auto",
            command.ReplacesExecutionOrderId?.ToString("N") ?? "none");
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));

        return $"exec_{Convert.ToHexStringLower(hash)[..48]}";
    }

    private static string BuildGateContext(string? requestContext, Guid orderId, string idempotencyKey)
    {
        var contextParts = new List<string>();

        if (!string.IsNullOrWhiteSpace(requestContext))
        {
            contextParts.Add(requestContext);
        }

        contextParts.Add($"ExecutionOrderId={orderId}");
        contextParts.Add($"IdempotencyKey={ShortHash(idempotencyKey)}");

        return string.Join(" | ", contextParts);
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

        if (command.StopLossPrice.HasValue)
        {
            var isValidStop = isBuy
                ? command.StopLossPrice.Value < command.Price
                : command.StopLossPrice.Value > command.Price;

            if (!isValidStop)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(command.StopLossPrice),
                    "StopLossPrice must be on the protective side of the entry price.");
            }
        }

        if (command.TakeProfitPrice.HasValue)
        {
            var isValidTakeProfit = isBuy
                ? command.TakeProfitPrice.Value > command.Price
                : command.TakeProfitPrice.Value < command.Price;

            if (!isValidTakeProfit)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(command.TakeProfitPrice),
                    "TakeProfitPrice must be on the favorable side of the entry price.");
            }
        }

        if (command.StopLossPrice.HasValue &&
            command.TakeProfitPrice.HasValue)
        {
            var hasConsistentBracket = isBuy
                ? command.StopLossPrice.Value < command.TakeProfitPrice.Value
                : command.StopLossPrice.Value > command.TakeProfitPrice.Value;

            if (!hasConsistentBracket)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(command.TakeProfitPrice),
                    "Protective bracket values are not internally consistent.");
            }
        }
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
}
