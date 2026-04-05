using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CoinBot.Application.Abstractions.Administration;
using CoinBot.Application.Abstractions.DemoPortfolio;
using CoinBot.Application.Abstractions.Execution;
using CoinBot.Application.Abstractions.ExchangeCredentials;
using CoinBot.Application.Abstractions.MarketData;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Execution;
using CoinBot.Infrastructure.Exchange;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CoinBot.Infrastructure.Administration;

public sealed class CrisisEscalationService(
    ApplicationDbContext dbContext,
    IGlobalSystemStateService globalSystemStateService,
    IExecutionEngine executionEngine,
    IExchangeCredentialService exchangeCredentialService,
    IBinancePrivateRestClient privateRestClient,
    ExecutionOrderLifecycleService executionOrderLifecycleService,
    IDemoPortfolioAccountingService demoPortfolioAccountingService,
    IMarketDataService marketDataService,
    ICrisisEscalationAuthorizationService authorizationService,
    ICrisisIncidentHook incidentHook,
    IOptions<DemoFillSimulatorOptions> demoFillOptions,
    TimeProvider timeProvider,
    ILogger<CrisisEscalationService> logger) : ICrisisEscalationService
{
    private const string CrisisSource = "AdminPortal.CrisisEscalation";
    private const string CrisisStrategyKey = "__crisis_flatten__";
    private static readonly IReadOnlySet<ExecutionOrderState> OpenOrderStates = new HashSet<ExecutionOrderState>
    {
        ExecutionOrderState.Received,
        ExecutionOrderState.GatePassed,
        ExecutionOrderState.Dispatching,
        ExecutionOrderState.Submitted,
        ExecutionOrderState.PartiallyFilled,
        ExecutionOrderState.CancelRequested
    };
    private static readonly string[] KnownQuoteAssets =
    [
        "USDT",
        "USDC",
        "BUSD",
        "BTC",
        "ETH",
        "BNB",
        "TRY",
        "EUR",
        "USD"
    ];

    public async Task<CrisisEscalationPreview> PreviewAsync(
        CrisisEscalationPreviewRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var scope = ParseScope(request.Level, request.Scope);
        return await BuildPreviewAsync(scope, cancellationToken);
    }

    public async Task<CrisisEscalationExecutionResult> ExecuteAsync(
        CrisisEscalationExecuteRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var scope = ParseScope(request.Level, request.Scope);
        var actorUserId = NormalizeRequired(request.ActorUserId, nameof(request.ActorUserId), 450);
        var executionActor = NormalizeRequired(request.ExecutionActor, nameof(request.ExecutionActor), 256);
        var commandId = NormalizeRequired(request.CommandId, nameof(request.CommandId), 128);
        var correlationId = NormalizeOptional(request.CorrelationId, 128);
        var normalizedReason = NormalizeOptional(request.Reason, 256);
        var normalizedMessage = NormalizeOptional(request.Message, 512);
        var normalizedReasonCode = ResolveReasonCode(scope.Level, request.ReasonCode);
        var preview = await BuildPreviewAsync(scope, cancellationToken);

        if (!string.Equals(preview.PreviewStamp, request.PreviewStamp?.Trim(), StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Impact preview guncel degil. Execute once more after refreshing preview.");
        }

        if (scope.Level == CrisisEscalationLevel.EmergencyFlatten)
        {
            var reason = NormalizeRequired(normalizedReason, nameof(request.Reason), 256);

            await authorizationService.ValidateReauthAsync(
                new CrisisReauthValidationRequest(
                    actorUserId,
                    scope.Level,
                    scope.ScopeKey,
                    request.ReauthToken ?? string.Empty,
                    correlationId),
                cancellationToken);

            await authorizationService.ValidateSecondApprovalAsync(
                new CrisisSecondApprovalValidationRequest(
                    actorUserId,
                    scope.Level,
                    scope.ScopeKey,
                    request.SecondApprovalReference ?? string.Empty,
                    reason,
                    preview,
                    correlationId),
                cancellationToken);

            normalizedReason = reason;
        }

        var effectiveReason = normalizedReason ?? "Crisis escalation";
        var purgedOrderCount = 0;
        var flattenAttemptCount = 0;
        var flattenReuseCount = 0;
        var failedOperationCount = 0;

        switch (scope.Level)
        {
            case CrisisEscalationLevel.SoftHalt:
                await ApplySoftHaltAsync(
                    actorUserId,
                    normalizedReasonCode,
                    normalizedMessage,
                    correlationId,
                    commandId,
                    request.RemoteIpAddress,
                    cancellationToken);
                break;
            case CrisisEscalationLevel.OrderPurge:
            {
                var purgeResult = await PurgeOpenOrdersAsync(
                    scope,
                    actorUserId,
                    executionActor,
                    commandId,
                    correlationId,
                    cancellationToken);
                purgedOrderCount = purgeResult.PurgedOrderCount;
                failedOperationCount = purgeResult.FailedOperationCount;
                break;
            }
            case CrisisEscalationLevel.EmergencyFlatten:
            {
                var purgeResult = await PurgeOpenOrdersAsync(
                    scope,
                    actorUserId,
                    executionActor,
                    commandId,
                    correlationId,
                    cancellationToken);
                purgedOrderCount = purgeResult.PurgedOrderCount;
                failedOperationCount += purgeResult.FailedOperationCount;

                var flattenResult = await FlattenPositionsAsync(
                    scope,
                    actorUserId,
                    executionActor,
                    commandId,
                    correlationId,
                    cancellationToken);
                flattenAttemptCount = flattenResult.FlattenAttemptCount;
                flattenReuseCount = flattenResult.FlattenReuseCount;
                failedOperationCount += flattenResult.FailedOperationCount;
                break;
            }
            default:
                throw new InvalidOperationException($"Unsupported crisis level '{scope.Level}'.");
        }

        var summary = BuildSummary(
            scope,
            purgedOrderCount,
            flattenAttemptCount,
            flattenReuseCount,
            failedOperationCount,
            effectiveReason);

        await incidentHook.WriteRecoveryAsync(
            new CrisisRecoveryHookRequest(
                actorUserId,
                scope.Level,
                scope.ScopeKey,
                summary,
                correlationId,
                commandId),
            cancellationToken);

        logger.LogWarning(
            "Crisis escalation {Level} completed for {Scope}. Purged={PurgedOrderCount}, Flattened={FlattenAttemptCount}, Reused={FlattenReuseCount}, Failures={FailedOperationCount}",
            scope.Level,
            scope.ScopeKey,
            purgedOrderCount,
            flattenAttemptCount,
            flattenReuseCount,
            failedOperationCount);

        return new CrisisEscalationExecutionResult(
            preview,
            purgedOrderCount,
            flattenAttemptCount,
            flattenReuseCount,
            failedOperationCount,
            summary);
    }

    private async Task ApplySoftHaltAsync(
        string actorUserId,
        string reasonCode,
        string? message,
        string? correlationId,
        string commandId,
        string? remoteIpAddress,
        CancellationToken cancellationToken)
    {
        await globalSystemStateService.SetStateAsync(
            new GlobalSystemStateSetRequest(
                GlobalSystemStateKind.SoftHalt,
                reasonCode,
                message,
                CrisisSource,
                correlationId,
                IsManualOverride: true,
                ExpiresAtUtc: null,
                UpdatedByUserId: actorUserId,
                UpdatedFromIp: NormalizeOptional(remoteIpAddress, 128),
                CommandId: commandId,
                ChangeSummary: "Crisis soft halt applied."),
            cancellationToken);
    }

    private async Task<(int PurgedOrderCount, int FailedOperationCount)> PurgeOpenOrdersAsync(
        CrisisScope scope,
        string actorUserId,
        string executionActor,
        string commandId,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        var orders = await QueryOpenOrders(scope)
            .Where(entity =>
                scope.Level != CrisisEscalationLevel.EmergencyFlatten ||
                entity.StrategyKey != CrisisStrategyKey)
            .ToListAsync(cancellationToken);
        var purgedOrderCount = 0;
        var failedOperationCount = 0;
        var affectedBotIds = new HashSet<Guid>();

        foreach (var order in orders)
        {
            if (order.BotId.HasValue)
            {
                affectedBotIds.Add(order.BotId.Value);
            }

            try
            {
                var purged = order.ExecutionEnvironment == ExecutionEnvironment.Demo
                    ? await PurgeDemoOrderAsync(order, correlationId, cancellationToken)
                    : await PurgeLiveOrderAsync(order, executionActor, commandId, correlationId, cancellationToken);

                if (purged)
                {
                    purgedOrderCount++;
                }
                else
                {
                    failedOperationCount++;
                    await WriteIncidentAsync(
                        actorUserId,
                        scope,
                        "Order purge left a live order unresolved.",
                        BuildOrderDetail(order),
                        correlationId,
                        commandId,
                        cancellationToken);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                failedOperationCount++;
                await WriteIncidentAsync(
                    actorUserId,
                    scope,
                    "Order purge failed.",
                    $"{BuildOrderDetail(order)} | Error={Truncate(exception.Message, 256)}",
                    correlationId,
                    commandId,
                    cancellationToken);
            }
        }

        await RefreshBotExposureCountsAsync(affectedBotIds, cancellationToken);
        return (purgedOrderCount, failedOperationCount);
    }

    private async Task<(int FlattenAttemptCount, int FlattenReuseCount, int FailedOperationCount)> FlattenPositionsAsync(
        CrisisScope scope,
        string actorUserId,
        string executionActor,
        string commandId,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        var flattenAttemptCount = 0;
        var flattenReuseCount = 0;
        var failedOperationCount = 0;
        var demoPositions = await QueryOpenDemoPositions(scope).ToListAsync(cancellationToken);
        var exchangePositions = await QueryOpenExchangePositions(scope).ToListAsync(cancellationToken);

        foreach (var position in demoPositions)
        {
            try
            {
                var positionHash = BuildDemoPositionHash(position);

                if (await HasReusableFlattenOrderAsync(
                        position.OwnerUserId,
                        position.Symbol,
                        ExecutionEnvironment.Demo,
                        positionHash,
                        cancellationToken))
                {
                    flattenReuseCount++;
                    continue;
                }

                var dispatchResult = await executionEngine.DispatchAsync(
                    await BuildDemoFlattenCommandAsync(
                        position,
                        executionActor,
                        commandId,
                        correlationId,
                        positionHash,
                        cancellationToken),
                    cancellationToken);

                if (dispatchResult.Order.State is ExecutionOrderState.Rejected or ExecutionOrderState.Failed)
                {
                    failedOperationCount++;
                    await WriteIncidentAsync(
                        actorUserId,
                        scope,
                        "Emergency flatten dispatch failed.",
                        $"{BuildDemoPositionDetail(position)} | State={dispatchResult.Order.State} | Failure={dispatchResult.Order.FailureDetail ?? dispatchResult.Order.FailureCode ?? "unknown"}",
                        correlationId,
                        commandId,
                        cancellationToken);
                    continue;
                }

                flattenAttemptCount++;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                failedOperationCount++;
                await WriteIncidentAsync(
                    actorUserId,
                    scope,
                    "Emergency flatten dispatch failed.",
                    $"{BuildDemoPositionDetail(position)} | Error={Truncate(exception.Message, 256)}",
                    correlationId,
                    commandId,
                    cancellationToken);
            }
        }

        foreach (var position in exchangePositions)
        {
            try
            {
                var positionHash = BuildExchangePositionHash(position);

                if (await HasReusableFlattenOrderAsync(
                        position.OwnerUserId,
                        position.Symbol,
                        ExecutionEnvironment.Live,
                        positionHash,
                        cancellationToken))
                {
                    flattenReuseCount++;
                    continue;
                }

                var dispatchResult = await executionEngine.DispatchAsync(
                    await BuildExchangeFlattenCommandAsync(
                        position,
                        executionActor,
                        commandId,
                        correlationId,
                        positionHash,
                        cancellationToken),
                    cancellationToken);

                if (dispatchResult.Order.State is ExecutionOrderState.Rejected or ExecutionOrderState.Failed)
                {
                    failedOperationCount++;
                    await WriteIncidentAsync(
                        actorUserId,
                        scope,
                        "Emergency flatten dispatch failed.",
                        $"{BuildExchangePositionDetail(position)} | State={dispatchResult.Order.State} | Failure={dispatchResult.Order.FailureDetail ?? dispatchResult.Order.FailureCode ?? "unknown"}",
                        correlationId,
                        commandId,
                        cancellationToken);
                    continue;
                }

                flattenAttemptCount++;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                failedOperationCount++;
                await WriteIncidentAsync(
                    actorUserId,
                    scope,
                    "Emergency flatten dispatch failed.",
                    $"{BuildExchangePositionDetail(position)} | Error={Truncate(exception.Message, 256)}",
                    correlationId,
                    commandId,
                    cancellationToken);
            }
        }

        return (flattenAttemptCount, flattenReuseCount, failedOperationCount);
    }

    private async Task<bool> PurgeDemoOrderAsync(
        ExecutionOrder order,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        if (order.State is ExecutionOrderState.Submitted or ExecutionOrderState.PartiallyFilled or ExecutionOrderState.CancelRequested)
        {
            await ReleaseOutstandingDemoReservationAsync(order, cancellationToken);
        }

        await LocalTerminalizeOrderAsync(
            order,
            order.State is ExecutionOrderState.Submitted or ExecutionOrderState.PartiallyFilled or ExecutionOrderState.CancelRequested
                ? ExecutionOrderState.Cancelled
                : ExecutionOrderState.Failed,
            order.State is ExecutionOrderState.Submitted or ExecutionOrderState.PartiallyFilled or ExecutionOrderState.CancelRequested
                ? "CrisisOrderPurgeCancelled"
                : "CrisisOrderPurgeFailedClosed",
            correlationId,
            cancellationToken);

        return true;
    }

    private async Task<bool> PurgeLiveOrderAsync(
        ExecutionOrder order,
        string executionActor,
        string commandId,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        if (!order.ExchangeAccountId.HasValue)
        {
            await LocalTerminalizeOrderAsync(
                order,
                ExecutionOrderState.Failed,
                "CrisisOrderPurgeFailedClosed",
                correlationId,
                cancellationToken);
            return true;
        }

        if (order.State is ExecutionOrderState.Received or ExecutionOrderState.GatePassed or ExecutionOrderState.Dispatching &&
            string.IsNullOrWhiteSpace(order.ExternalOrderId))
        {
            await LocalTerminalizeOrderAsync(
                order,
                ExecutionOrderState.Failed,
                "CrisisOrderPurgeFailedClosed",
                correlationId,
                cancellationToken);
            return true;
        }

        var credentials = await exchangeCredentialService.GetAsync(
            new ExchangeCredentialAccessRequest(
                order.ExchangeAccountId.Value,
                executionActor,
                ExchangeCredentialAccessPurpose.Execution,
                correlationId),
            cancellationToken);
        var snapshot = await privateRestClient.CancelOrderAsync(
            new BinanceOrderCancelRequest(
                order.ExchangeAccountId.Value,
                order.Symbol,
                order.ExternalOrderId,
                ExecutionClientOrderId.Create(order.Id),
                credentials.ApiKey,
                credentials.ApiSecret,
                commandId,
                correlationId,
                order.Id,
                order.OwnerUserId),
            cancellationToken);

        await executionOrderLifecycleService.ApplyExchangeUpdateAsync(snapshot, cancellationToken);

        var currentState = await dbContext.ExecutionOrders
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(entity => entity.Id == order.Id && !entity.IsDeleted)
            .Select(entity => entity.State)
            .SingleAsync(cancellationToken);

        return currentState is ExecutionOrderState.Cancelled or
            ExecutionOrderState.Rejected or
            ExecutionOrderState.Failed;
    }

    private async Task LocalTerminalizeOrderAsync(
        ExecutionOrder order,
        ExecutionOrderState targetState,
        string eventCode,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        var lastTransition = await dbContext.ExecutionOrderTransitions
            .IgnoreQueryFilters()
            .Where(entity => entity.ExecutionOrderId == order.Id && !entity.IsDeleted)
            .OrderByDescending(entity => entity.SequenceNumber)
            .FirstOrDefaultAsync(cancellationToken);
        var occurredAtUtc = timeProvider.GetUtcNow().UtcDateTime;

        dbContext.ExecutionOrderTransitions.Add(
            ExecutionOrderStateMachine.Transition(
                order,
                (lastTransition?.SequenceNumber ?? 0) + 1,
                targetState,
                eventCode,
                occurredAtUtc,
                CreateCorrelationId(),
                lastTransition?.CorrelationId ?? correlationId ?? order.RootCorrelationId,
                $"Crisis action terminalized pending order. TargetState={targetState}"));

        if (targetState == ExecutionOrderState.Failed)
        {
            order.FailureCode = "CrisisOrderPurge";
            order.FailureDetail = "Pending order failed closed by crisis purge.";
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task ReleaseOutstandingDemoReservationAsync(
        ExecutionOrder order,
        CancellationToken cancellationToken)
    {
        var remainingQuantity = Math.Max(0m, order.Quantity - order.FilledQuantity);

        if (remainingQuantity <= 0m)
        {
            return;
        }

        var amount = order.Side == ExecutionOrderSide.Buy
            ? remainingQuantity * order.Price * (1m + ResolveDemoFeeRate(order.OrderType))
            : remainingQuantity;

        if (amount <= 0m)
        {
            return;
        }

        await demoPortfolioAccountingService.ReleaseFundsAsync(
            new DemoFundsReleaseRequest(
                order.OwnerUserId,
                ExecutionEnvironment.Demo,
                $"crisis-purge-release:{order.Id:N}",
                order.Side == ExecutionOrderSide.Buy ? order.QuoteAsset : order.BaseAsset,
                amount,
                order.ExternalOrderId ?? order.Id.ToString("N"),
                timeProvider.GetUtcNow().UtcDateTime),
            cancellationToken);
    }

    private async Task<ExecutionCommand> BuildDemoFlattenCommandAsync(
        DemoPosition position,
        string executionActor,
        string commandId,
        string? correlationId,
        string positionHash,
        CancellationToken cancellationToken)
    {
        var price = ResolveDemoPositionPrice(position);
        var (baseAsset, quoteAsset) = await ResolveAssetsAsync(
            position.Symbol,
            position.BaseAsset,
            position.QuoteAsset,
            cancellationToken);

        return new ExecutionCommand(
            executionActor,
            position.OwnerUserId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            StrategySignalType.Exit,
            CrisisStrategyKey,
            position.Symbol,
            "admin",
            baseAsset,
            quoteAsset,
            ResolveExitSide(position.Quantity),
            ExecutionOrderType.Market,
            Math.Abs(position.Quantity),
            price,
            BotId: position.BotId,
            ExchangeAccountId: null,
            IsDemo: true,
            IdempotencyKey: BuildCrisisExitIdempotencyKey(positionHash, commandId),
            CorrelationId: correlationId,
            Context: $"CrisisEmergencyFlatten|PositionHash={positionHash}",
            AdministrativeOverride: true,
            AdministrativeOverrideReason: $"CrisisEmergencyFlatten|PositionHash={positionHash}");
    }

    private async Task<ExecutionCommand> BuildExchangeFlattenCommandAsync(
        ExchangePosition position,
        string executionActor,
        string commandId,
        string? correlationId,
        string positionHash,
        CancellationToken cancellationToken)
    {
        var price = ResolveExchangePositionPrice(position);
        var (baseAsset, quoteAsset) = await ResolveAssetsAsync(
            position.Symbol,
            fallbackBaseAsset: null,
            fallbackQuoteAsset: null,
            cancellationToken);

        return new ExecutionCommand(
            executionActor,
            position.OwnerUserId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            StrategySignalType.Exit,
            CrisisStrategyKey,
            position.Symbol,
            "admin",
            baseAsset,
            quoteAsset,
            ResolveExitSide(position.Quantity),
            ExecutionOrderType.Market,
            Math.Abs(position.Quantity),
            price,
            BotId: null,
            ExchangeAccountId: position.ExchangeAccountId,
            IsDemo: false,
            IdempotencyKey: BuildCrisisExitIdempotencyKey(positionHash, commandId),
            CorrelationId: correlationId,
            Context: $"CrisisEmergencyFlatten|PositionHash={positionHash}",
            AdministrativeOverride: true,
            AdministrativeOverrideReason: $"CrisisEmergencyFlatten|PositionHash={positionHash}");
    }

    private async Task<bool> HasReusableFlattenOrderAsync(
        string ownerUserId,
        string symbol,
        ExecutionEnvironment environment,
        string positionHash,
        CancellationToken cancellationToken)
    {
        var prefix = BuildCrisisExitKeyPrefix(positionHash);

        return await dbContext.ExecutionOrders
            .IgnoreQueryFilters()
            .AsNoTracking()
            .AnyAsync(
                entity =>
                    entity.OwnerUserId == ownerUserId &&
                    entity.Symbol == symbol &&
                    entity.ExecutionEnvironment == environment &&
                    entity.StrategyKey == CrisisStrategyKey &&
                    entity.IdempotencyKey.StartsWith(prefix) &&
                    !entity.IsDeleted &&
                    entity.State != ExecutionOrderState.Failed &&
                    entity.State != ExecutionOrderState.Rejected &&
                    entity.State != ExecutionOrderState.Cancelled,
                cancellationToken);
    }

    private async Task<CrisisEscalationPreview> BuildPreviewAsync(
        CrisisScope scope,
        CancellationToken cancellationToken)
    {
        var openOrders = await QueryOpenOrders(scope)
            .AsNoTracking()
            .Select(entity => new { entity.OwnerUserId, entity.Symbol })
            .ToListAsync(cancellationToken);
        var demoPositions = await QueryOpenDemoPositions(scope)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        var exchangePositions = await QueryOpenExchangePositions(scope)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        var affectedUsers = new HashSet<string>(StringComparer.Ordinal);
        var affectedSymbols = new HashSet<string>(StringComparer.Ordinal);

        foreach (var order in openOrders)
        {
            affectedUsers.Add(order.OwnerUserId);
            affectedSymbols.Add(order.Symbol);
        }

        foreach (var position in demoPositions)
        {
            affectedUsers.Add(position.OwnerUserId);
            affectedSymbols.Add(position.Symbol);
        }

        foreach (var position in exchangePositions)
        {
            affectedUsers.Add(position.OwnerUserId);
            affectedSymbols.Add(position.Symbol);
        }

        var preview = new CrisisEscalationPreview(
            scope.Level,
            scope.ScopeKey,
            affectedUsers.Count,
            affectedSymbols.Count,
            demoPositions.Count + exchangePositions.Count,
            openOrders.Count,
            demoPositions.Sum(CalculateDemoExposure) + exchangePositions.Sum(CalculateExchangeExposure),
            RequiresReauth: scope.Level == CrisisEscalationLevel.EmergencyFlatten,
            RequiresSecondApproval: scope.Level == CrisisEscalationLevel.EmergencyFlatten,
            PreviewStamp: string.Empty);

        return preview with
        {
            PreviewStamp = BuildPreviewStamp(preview)
        };
    }

    private IQueryable<ExecutionOrder> QueryOpenOrders(CrisisScope scope)
    {
        var query = dbContext.ExecutionOrders
            .IgnoreQueryFilters()
            .Where(entity =>
                !entity.IsDeleted &&
                OpenOrderStates.Contains(entity.State));

        return scope.IsGlobal
            ? query
            : query.Where(entity => entity.OwnerUserId == scope.UserId);
    }

    private IQueryable<DemoPosition> QueryOpenDemoPositions(CrisisScope scope)
    {
        var query = dbContext.DemoPositions
            .IgnoreQueryFilters()
            .Where(entity =>
                !entity.IsDeleted &&
                entity.Quantity != 0m);

        return scope.IsGlobal
            ? query
            : query.Where(entity => entity.OwnerUserId == scope.UserId);
    }

    private IQueryable<ExchangePosition> QueryOpenExchangePositions(CrisisScope scope)
    {
        var query = dbContext.ExchangePositions
            .IgnoreQueryFilters()
            .Where(entity =>
                !entity.IsDeleted &&
                entity.Plane == ExchangeDataPlane.Futures &&
                entity.Quantity != 0m);

        return scope.IsGlobal
            ? query
            : query.Where(entity => entity.OwnerUserId == scope.UserId);
    }

    private async Task RefreshBotExposureCountsAsync(
        IReadOnlyCollection<Guid> botIds,
        CancellationToken cancellationToken)
    {
        if (botIds.Count == 0)
        {
            return;
        }

        var bots = await dbContext.TradingBots
            .IgnoreQueryFilters()
            .Where(entity => botIds.Contains(entity.Id) && !entity.IsDeleted)
            .ToListAsync(cancellationToken);

        foreach (var bot in bots)
        {
            bot.OpenOrderCount = await dbContext.ExecutionOrders
                .IgnoreQueryFilters()
                .CountAsync(
                    entity =>
                        entity.BotId == bot.Id &&
                        !entity.IsDeleted &&
                        OpenOrderStates.Contains(entity.State),
                    cancellationToken);

            bot.OpenPositionCount = await dbContext.DemoPositions
                .IgnoreQueryFilters()
                .CountAsync(
                    entity =>
                        entity.BotId == bot.Id &&
                        !entity.IsDeleted &&
                        entity.Quantity != 0m,
                    cancellationToken);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<(string BaseAsset, string QuoteAsset)> ResolveAssetsAsync(
        string symbol,
        string? fallbackBaseAsset,
        string? fallbackQuoteAsset,
        CancellationToken cancellationToken)
    {
        var metadata = await marketDataService.GetSymbolMetadataAsync(symbol, cancellationToken);

        if (metadata is not null)
        {
            return (metadata.BaseAsset, metadata.QuoteAsset);
        }

        if (!string.IsNullOrWhiteSpace(fallbackBaseAsset) &&
            !string.IsNullOrWhiteSpace(fallbackQuoteAsset))
        {
            return (fallbackBaseAsset.Trim().ToUpperInvariant(), fallbackQuoteAsset.Trim().ToUpperInvariant());
        }

        var normalizedSymbol = symbol.Trim().ToUpperInvariant();

        foreach (var quoteAsset in KnownQuoteAssets)
        {
            if (!normalizedSymbol.EndsWith(quoteAsset, StringComparison.Ordinal))
            {
                continue;
            }

            var baseAsset = normalizedSymbol[..^quoteAsset.Length];

            if (!string.IsNullOrWhiteSpace(baseAsset))
            {
                return (baseAsset, quoteAsset);
            }
        }

        throw new InvalidOperationException($"Assets could not be resolved for symbol '{symbol}'.");
    }

    private async Task WriteIncidentAsync(
        string actorUserId,
        CrisisScope scope,
        string summary,
        string detail,
        string? correlationId,
        string? commandId,
        CancellationToken cancellationToken)
    {
        await incidentHook.WriteIncidentAsync(
            new CrisisIncidentHookRequest(
                actorUserId,
                scope.Level,
                scope.ScopeKey,
                summary,
                detail,
                correlationId,
                commandId),
            cancellationToken);
    }

    private static CrisisScope ParseScope(CrisisEscalationLevel level, string? scope)
    {
        var rawScope = NormalizeRequired(scope, nameof(scope), 128);
        var normalizedScope = rawScope.ToUpperInvariant();

        return level switch
        {
            CrisisEscalationLevel.SoftHalt when normalizedScope == "GLOBAL_SOFT_HALT" =>
                new CrisisScope(level, "GLOBAL_SOFT_HALT", IsGlobal: true, UserId: null),
            CrisisEscalationLevel.OrderPurge when normalizedScope == "GLOBAL_PURGE" =>
                new CrisisScope(level, "GLOBAL_PURGE", IsGlobal: true, UserId: null),
            CrisisEscalationLevel.EmergencyFlatten when normalizedScope == "GLOBAL_FLATTEN" =>
                new CrisisScope(level, "GLOBAL_FLATTEN", IsGlobal: true, UserId: null),
            CrisisEscalationLevel.OrderPurge when rawScope.StartsWith("PURGE:USER:", StringComparison.OrdinalIgnoreCase) =>
                new CrisisScope(level, $"PURGE:USER:{NormalizeUserScope(rawScope, "PURGE:USER:")}", IsGlobal: false, UserId: NormalizeUserScope(rawScope, "PURGE:USER:")),
            CrisisEscalationLevel.EmergencyFlatten when rawScope.StartsWith("FLATTEN:USER:", StringComparison.OrdinalIgnoreCase) =>
                new CrisisScope(level, $"FLATTEN:USER:{NormalizeUserScope(rawScope, "FLATTEN:USER:")}", IsGlobal: false, UserId: NormalizeUserScope(rawScope, "FLATTEN:USER:")),
            _ => throw new InvalidOperationException($"Scope '{scope}' is not valid for level '{level}'.")
        };
    }

    private static string NormalizeUserScope(string scope, string prefix)
    {
        var userId = scope[prefix.Length..].Trim();

        if (string.IsNullOrWhiteSpace(userId))
        {
            throw new InvalidOperationException("User-scoped crisis command requires a user id.");
        }

        return userId;
    }

    private static string ResolveReasonCode(CrisisEscalationLevel level, string? reasonCode)
    {
        var normalizedReasonCode = NormalizeOptional(reasonCode, 64);

        if (!string.IsNullOrWhiteSpace(normalizedReasonCode))
        {
            return normalizedReasonCode;
        }

        return level switch
        {
            CrisisEscalationLevel.SoftHalt => "CRISIS_SOFT_HALT",
            CrisisEscalationLevel.OrderPurge => "CRISIS_ORDER_PURGE",
            CrisisEscalationLevel.EmergencyFlatten => "CRISIS_EMERGENCY_FLATTEN",
            _ => "CRISIS_ACTION"
        };
    }

    private static string BuildSummary(
        CrisisScope scope,
        int purgedOrderCount,
        int flattenAttemptCount,
        int flattenReuseCount,
        int failedOperationCount,
        string reason)
    {
        var summary = string.Join(
            " | ",
            $"Level={scope.Level}",
            $"Scope={scope.ScopeKey}",
            $"PurgedOrders={purgedOrderCount}",
            $"FlattenDispatches={flattenAttemptCount}",
            $"FlattenReused={flattenReuseCount}",
            $"Failures={failedOperationCount}",
            $"Reason={reason}");

        return summary.Length <= 512
            ? summary
            : summary[..512];
    }

    private static string BuildPreviewStamp(CrisisEscalationPreview preview)
    {
        var payload = string.Join(
            "|",
            preview.Level,
            preview.Scope,
            preview.AffectedUserCount,
            preview.AffectedSymbolCount,
            preview.OpenPositionCount,
            preview.PendingOrderCount,
            preview.EstimatedExposure.ToString("0.##################", CultureInfo.InvariantCulture),
            preview.RequiresReauth,
            preview.RequiresSecondApproval);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));

        return Convert.ToHexStringLower(hash);
    }

    private static string BuildCrisisExitKeyPrefix(string positionHash)
    {
        return $"cfx_{positionHash}_";
    }

    private static string BuildCrisisExitIdempotencyKey(string positionHash, string commandId)
    {
        return $"{BuildCrisisExitKeyPrefix(positionHash)}{ShortHash(commandId, 10)}";
    }

    private static string BuildDemoPositionHash(DemoPosition position)
    {
        var payload = string.Join(
            "|",
            "demo",
            position.OwnerUserId,
            position.Symbol,
            position.Quantity.ToString("0.##################", CultureInfo.InvariantCulture),
            position.AverageEntryPrice.ToString("0.##################", CultureInfo.InvariantCulture),
            position.LastFilledAtUtc?.ToString("O") ?? "none",
            position.LastValuationAtUtc?.ToString("O") ?? "none");

        return ShortHash(payload, 24);
    }

    private static string BuildExchangePositionHash(ExchangePosition position)
    {
        var payload = string.Join(
            "|",
            "live",
            position.OwnerUserId,
            position.ExchangeAccountId,
            position.Symbol,
            position.PositionSide,
            position.Quantity.ToString("0.##################", CultureInfo.InvariantCulture),
            position.EntryPrice.ToString("0.##################", CultureInfo.InvariantCulture),
            position.ExchangeUpdatedAtUtc.ToString("O"),
            position.SyncedAtUtc.ToString("O"));

        return ShortHash(payload, 24);
    }

    private static string BuildOrderDetail(ExecutionOrder order)
    {
        return $"OrderId={order.Id:N}; UserId={order.OwnerUserId}; Symbol={order.Symbol}; Environment={order.ExecutionEnvironment}; State={order.State}";
    }

    private static string BuildDemoPositionDetail(DemoPosition position)
    {
        return $"UserId={position.OwnerUserId}; Symbol={position.Symbol}; Quantity={position.Quantity.ToString("0.##################", CultureInfo.InvariantCulture)}";
    }

    private static string BuildExchangePositionDetail(ExchangePosition position)
    {
        return $"UserId={position.OwnerUserId}; ExchangeAccountId={position.ExchangeAccountId}; Symbol={position.Symbol}; Quantity={position.Quantity.ToString("0.##################", CultureInfo.InvariantCulture)}";
    }

    private decimal ResolveDemoFeeRate(ExecutionOrderType orderType)
    {
        return (orderType == ExecutionOrderType.Market
                ? demoFillOptions.Value.TakerFeeBps
                : demoFillOptions.Value.MakerFeeBps) /
            10000m;
    }

    private static decimal CalculateDemoExposure(DemoPosition position)
    {
        if (position.PositionKind == DemoPositionKind.Futures)
        {
            if (position.LastMarkPrice is decimal futuresMarkPrice && futuresMarkPrice > 0m)
            {
                return Math.Abs(futuresMarkPrice * position.Quantity);
            }

            if (position.LastPrice is decimal futuresLastPrice && futuresLastPrice > 0m)
            {
                return Math.Abs(futuresLastPrice * position.Quantity);
            }
        }

        if (position.LastMarkPrice is decimal markPrice && markPrice > 0m)
        {
            return Math.Abs(markPrice * position.Quantity);
        }

        if (position.AverageEntryPrice > 0m)
        {
            return Math.Abs(position.AverageEntryPrice * position.Quantity);
        }

        return Math.Abs(position.CostBasis + position.UnrealizedPnl);
    }

    private static decimal CalculateExchangeExposure(ExchangePosition position)
    {
        var referencePrice = position.EntryPrice > 0m
            ? position.EntryPrice
            : position.BreakEvenPrice > 0m
                ? position.BreakEvenPrice
                : 0m;

        return Math.Abs(referencePrice * position.Quantity);
    }

    private static decimal ResolveDemoPositionPrice(DemoPosition position)
    {
        return position.LastMarkPrice is decimal markPrice && markPrice > 0m
            ? markPrice
            : position.LastPrice is decimal lastPrice && lastPrice > 0m
                ? lastPrice
                : position.AverageEntryPrice > 0m
                    ? position.AverageEntryPrice
                    : 1m;
    }

    private static decimal ResolveExchangePositionPrice(ExchangePosition position)
    {
        return position.EntryPrice > 0m
            ? position.EntryPrice
            : position.BreakEvenPrice > 0m
                ? position.BreakEvenPrice
                : 1m;
    }

    private static ExecutionOrderSide ResolveExitSide(decimal quantity)
    {
        return quantity > 0m
            ? ExecutionOrderSide.Sell
            : ExecutionOrderSide.Buy;
    }

    private static string ShortHash(string value, int length)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexStringLower(hash)[..length];
    }

    private static string CreateCorrelationId()
    {
        return Guid.NewGuid().ToString("N");
    }

    private static string NormalizeRequired(string? value, string parameterName, int maxLength)
    {
        var normalizedValue = NormalizeOptional(value, maxLength);

        if (string.IsNullOrWhiteSpace(normalizedValue))
        {
            throw new ArgumentException("The value is required.", parameterName);
        }

        return normalizedValue;
    }

    private static string? NormalizeOptional(string? value, int maxLength)
    {
        var normalizedValue = value?.Trim();

        if (string.IsNullOrWhiteSpace(normalizedValue))
        {
            return null;
        }

        return normalizedValue.Length <= maxLength
            ? normalizedValue
            : throw new ArgumentOutOfRangeException(nameof(value), $"The value cannot exceed {maxLength} characters.");
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

    private sealed record CrisisScope(
        CrisisEscalationLevel Level,
        string ScopeKey,
        bool IsGlobal,
        string? UserId);
}
