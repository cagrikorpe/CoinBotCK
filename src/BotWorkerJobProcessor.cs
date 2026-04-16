using System.Globalization;
using System.Text.Json;
using CoinBot.Application.Abstractions.Ai;
using CoinBot.Application.Abstractions.Administration;
using CoinBot.Application.Abstractions.Execution;
using CoinBot.Application.Abstractions.Features;
using CoinBot.Application.Abstractions.Risk;
using CoinBot.Application.Abstractions.Indicators;
using CoinBot.Application.Abstractions.MarketData;
using CoinBot.Application.Abstractions.Strategies;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Ai;
using CoinBot.Infrastructure.Execution;
using CoinBot.Infrastructure.MarketData;
using CoinBot.Infrastructure.Observability;
using CoinBot.Infrastructure.Persistence;
using CoinBot.Infrastructure.Strategies;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CoinBot.Infrastructure.Jobs;

public sealed class BotWorkerJobProcessor(
    ApplicationDbContext dbContext,
    IIndicatorDataService indicatorDataService,
    IMarketDataService marketDataService,
    IBinanceExchangeInfoClient exchangeInfoClient,
    IBinanceHistoricalKlineClient historicalKlineClient,
    IStrategySignalService strategySignalService,
    IExecutionEngine executionEngine,
    IExecutionGate executionGate,
    IUserExecutionOverrideGuard userExecutionOverrideGuard,
    IDataLatencyCircuitBreaker dataLatencyCircuitBreaker,
    ITradingFeatureSnapshotService featureSnapshotService,
    IAiShadowDecisionService aiShadowDecisionService,
    ITraceService traceService,
    ICorrelationContextAccessor correlationContextAccessor,
    IOptions<BotExecutionPilotOptions> options,
    IOptions<AiSignalOptions> aiSignalOptions,
    IHostEnvironment hostEnvironment,
    TimeProvider timeProvider,
    ILogger<BotWorkerJobProcessor> logger) : IBotWorkerJobProcessor
{
    private const string ExitSkippedDecisionOutcome = "Skipped";
    private const string ExitSkippedDecisionReasonType = "ExecutionSkip";
    private const string NoOpenPositionForExitDecisionCode = "NoOpenPositionForExit";
    private const string NoClosableQuantityForExitDecisionCode = "NoClosableQuantityForExit";

    private static readonly ExecutionOrderState[] ExistingSignalTerminalStates =
    [
        ExecutionOrderState.Received,
        ExecutionOrderState.GatePassed,
        ExecutionOrderState.Dispatching,
        ExecutionOrderState.Submitted,
        ExecutionOrderState.PartiallyFilled,
        ExecutionOrderState.Filled
    ];

    private readonly BotExecutionPilotOptions optionsValue = options.Value;
    private readonly AiSignalOptions aiSignalOptionsValue = aiSignalOptions.Value;

    public async Task<BackgroundJobProcessResult> ProcessAsync(
        TradingBot bot,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bot);
        cancellationToken.ThrowIfCancellationRequested();

        if (!optionsValue.Enabled)
        {
            logger.LogWarning("Bot execution pilot is disabled. BotId={BotId}", bot.Id);
            return BackgroundJobProcessResult.PermanentFailure("PilotDisabled");
        }

        if (!IsPilotHostAllowed())
        {
            logger.LogWarning("Bot execution pilot is restricted to explicit pilot hosts. BotId={BotId}", bot.Id);
            return BackgroundJobProcessResult.PermanentFailure("PilotRequiresDevelopment");
        }

        var normalizedSymbol = MarketDataSymbolNormalizer.Normalize(
            string.IsNullOrWhiteSpace(bot.Symbol)
                ? optionsValue.DefaultSymbol
                : bot.Symbol);
        var timeframe = NormalizeRequired(optionsValue.Timeframe, nameof(optionsValue.Timeframe));

        if (!IsAllowedSymbol(normalizedSymbol))
        {
            logger.LogWarning(
                "Bot execution pilot rejected BotId {BotId} because symbol {Symbol} is outside the allowed pilot symbol set.",
                bot.Id,
                normalizedSymbol);
            return BackgroundJobProcessResult.PermanentFailure("PilotSymbolNotAllowed");
        }

        var sameSymbolEnabledBotCount = (await dbContext.TradingBots
                .IgnoreQueryFilters()
                .Where(entity =>
                    entity.OwnerUserId == bot.OwnerUserId &&
                    entity.IsEnabled &&
                    !entity.IsDeleted)
                .Select(entity => entity.Symbol)
                .ToListAsync(cancellationToken))
            .Select(symbol => MarketDataSymbolNormalizer.Normalize(
                string.IsNullOrWhiteSpace(symbol)
                    ? optionsValue.DefaultSymbol
                    : symbol))
            .Count(symbol => string.Equals(symbol, normalizedSymbol, StringComparison.Ordinal));

        if (sameSymbolEnabledBotCount != 1)
        {
            logger.LogWarning(
                "Bot execution pilot rejected BotId {BotId} because symbol {Symbol} has conflicting enabled bots for the same owner. EnabledSameSymbolBotCount={EnabledSameSymbolBotCount}",
                bot.Id,
                normalizedSymbol,
                sameSymbolEnabledBotCount);
            return BackgroundJobProcessResult.PermanentFailure("PilotSymbolConflictMultipleEnabledBots");
        }

        if (!TryResolvePilotExecutionParameters(bot, out var leverage, out var marginType, out var parameterFailureCode))
        {
            logger.LogWarning(
                "Bot execution pilot rejected BotId {BotId} because pilot parameters are invalid. FailureCode={FailureCode}",
                bot.Id,
                parameterFailureCode);
            return BackgroundJobProcessResult.PermanentFailure(parameterFailureCode ?? "PilotParametersInvalid");
        }

        var exchangeAccount = await ResolveExchangeAccountAsync(bot, cancellationToken);

        if (exchangeAccount is null)
        {
            logger.LogWarning(
                "Bot execution pilot rejected BotId {BotId} because no eligible exchange account was available.",
                bot.Id);
            return BackgroundJobProcessResult.PermanentFailure("ExchangeAccountSelectionRequired");
        }

        var publishedVersion = await ResolvePublishedStrategyVersionAsync(bot, cancellationToken);

        if (publishedVersion is null)
        {
            logger.LogWarning(
                "Bot execution pilot rejected BotId {BotId} because no published strategy version was found for StrategyKey {StrategyKey}.",
                bot.Id,
                bot.StrategyKey);
            return BackgroundJobProcessResult.PermanentFailure("PublishedStrategyVersionMissing");
        }

        try
        {
            await marketDataService.TrackSymbolAsync(normalizedSymbol, cancellationToken);
            await indicatorDataService.TrackSymbolAsync(normalizedSymbol, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                exception,
                "Bot execution pilot failed while tracking symbol {Symbol} for BotId {BotId}.",
                normalizedSymbol,
                bot.Id);
            return BackgroundJobProcessResult.RetryableFailure("SymbolTrackingUnavailable");
        }

        var symbolMetadata = await ResolveSymbolMetadataAsync(normalizedSymbol, cancellationToken);

        if (symbolMetadata is null)
        {
            logger.LogWarning(
                "Bot execution pilot could not resolve symbol metadata for {Symbol}. BotId={BotId}",
                normalizedSymbol,
                bot.Id);
            return BackgroundJobProcessResult.RetryableFailure("SymbolMetadataUnavailable");
        }

        var marketState = await ResolveMarketStateAsync(normalizedSymbol, timeframe, cancellationToken);
        var featureSnapshot = await TryCaptureFeatureSnapshotAsync(bot, exchangeAccount, normalizedSymbol, timeframe, marketState, cancellationToken);

        if (marketState.IndicatorSnapshot is null)
        {
            logger.LogInformation(
                "Bot execution pilot skipped BotId {BotId} because indicator data is not ready for {Symbol} {Timeframe}.",
                bot.Id,
                normalizedSymbol,
                timeframe);
            return BackgroundJobProcessResult.RetryableFailure("IndicatorSnapshotUnavailable");
        }

        if (!marketState.ReferencePrice.HasValue || marketState.ReferencePrice.Value <= 0m)
        {
            logger.LogWarning(
                "Bot execution pilot rejected BotId {BotId} because notional data is unavailable for {Symbol}. PilotActivationEnabled={PilotActivationEnabled}",
                bot.Id,
                normalizedSymbol,
                optionsValue.PilotActivationEnabled);
            return optionsValue.PilotActivationEnabled
                ? BackgroundJobProcessResult.PermanentFailure("UserExecutionPilotNotionalDataUnavailable")
                : BackgroundJobProcessResult.RetryableFailure("ReferencePriceUnavailable");
        }

        var correlationId = Guid.NewGuid().ToString("N");
        using var correlationScope = correlationContextAccessor.BeginScope(
            new CorrelationContext(
                correlationId,
                $"bot-job:{bot.Id:N}",
                correlationId,
                null));

        StrategySignalGenerationResult signalGenerationResult;

        try
        {
            signalGenerationResult = await strategySignalService.GenerateAsync(
                new GenerateStrategySignalsRequest(
                    publishedVersion.Id,
                    new StrategyEvaluationContext(
                        optionsValue.SignalEvaluationMode,
                        marketState.IndicatorSnapshot),
                    featureSnapshot),
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                exception,
                "Bot execution pilot failed while generating a strategy signal for BotId {BotId}.",
                bot.Id);
            return BackgroundJobProcessResult.RetryableFailure("SignalGenerationFailed");
        }

        var currentNetQuantity = await ResolveCurrentNetQuantityAsync(
            bot.OwnerUserId,
            exchangeAccount.Id,
            normalizedSymbol,
            cancellationToken);
        var signal = await ResolveActionableSignalAsync(
            signalGenerationResult,
            publishedVersion.Id,
            normalizedSymbol,
            timeframe,
            marketState.IndicatorSnapshot.CloseTimeUtc,
            preferExitSignal: currentNetQuantity != 0m,
            cancellationToken);
        var strategyDecisionTrace = await ResolveLatestDecisionTraceAsync(
            correlationId,
            bot.OwnerUserId,
            normalizedSymbol,
            timeframe,
            cancellationToken);
        var pilotActivationEnabled = optionsValue.PilotActivationEnabled;
        var shadowModeActive = aiSignalOptionsValue.Enabled &&
                               aiSignalOptionsValue.ShadowModeEnabled &&
                               !pilotActivationEnabled;

        if (shadowModeActive)
        {
            try
            {
                var shadowDecision = await CaptureShadowDecisionAsync(
                    bot,
                    exchangeAccount,
                    publishedVersion,
                    normalizedSymbol,
                    timeframe,
                    symbolMetadata,
                    marketState,
                    featureSnapshot,
                    signalGenerationResult,
                    signal,
                    strategyDecisionTrace,
                    correlationId,
                    marginType!,
                    leverage!.Value,
                    cancellationToken);

                logger.LogInformation(
                    "Bot execution pilot persisted AI shadow decision {ShadowDecisionId} for BotId {BotId}. FinalAction={FinalAction} HypotheticalSubmitAllowed={HypotheticalSubmitAllowed} NoSubmitReason={NoSubmitReason}.",
                    shadowDecision.Id,
                    bot.Id,
                    shadowDecision.FinalAction,
                    shadowDecision.HypotheticalSubmitAllowed,
                    shadowDecision.NoSubmitReason);

                return BackgroundJobProcessResult.Success();
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(
                    exception,
                    "Bot execution pilot failed while capturing an AI shadow decision for BotId {BotId}.",
                    bot.Id);
                return BackgroundJobProcessResult.RetryableFailure("AiShadowDecisionCaptureFailed");
            }
        }

        if (!pilotActivationEnabled)
        {
            logger.LogInformation(
                "Bot execution pilot skipped submit for BotId {BotId} because PilotActivationEnabled is false. Symbol={Symbol} Timeframe={Timeframe} HasActionableSignal={HasActionableSignal}.",
                bot.Id,
                normalizedSymbol,
                timeframe,
                signal is not null);
            return BackgroundJobProcessResult.Success();
        }
        if (signal is null)
        {
            logger.LogInformation(
                "Bot execution pilot found no actionable signal for BotId {BotId}. Signals={SignalCount} Vetoes={VetoCount} Suppressed={SuppressedDuplicateCount}",
                bot.Id,
                signalGenerationResult.Signals.Count,
                signalGenerationResult.Vetoes.Count,
                signalGenerationResult.SuppressedDuplicateCount);
            return BackgroundJobProcessResult.Success();
        }

        var existingExecutionExists = await dbContext.ExecutionOrders
            .IgnoreQueryFilters()
            .AnyAsync(
                entity => entity.StrategySignalId == signal.StrategySignalId &&
                          !entity.IsDeleted &&
                          ExistingSignalTerminalStates.Contains(entity.State),
                cancellationToken);

        if (existingExecutionExists)
        {
            logger.LogInformation(
                "Bot execution pilot suppressed a duplicate order for BotId {BotId} and StrategySignalId {StrategySignalId}.",
                bot.Id,
                signal.StrategySignalId);
            return BackgroundJobProcessResult.Success();
        }

        if (signal.SignalType == StrategySignalType.Exit && currentNetQuantity == 0m)
        {
            await WriteExitSkippedDecisionTraceAsync(
                bot.OwnerUserId,
                publishedVersion,
                signal,
                correlationId,
                strategyDecisionTrace,
                NoOpenPositionForExitDecisionCode,
                $"Exit signal was skipped because no open position exists for {signal.Symbol} on the selected exchange account.",
                currentNetQuantity,
                cancellationToken);

            logger.LogInformation(
                "Bot execution pilot skipped exit dispatch for BotId {BotId} because no open position exists for {Symbol}. StrategySignalId={StrategySignalId}.",
                bot.Id,
                signal.Symbol,
                signal.StrategySignalId);
            return BackgroundJobProcessResult.Success();
        }

        PilotDispatchPlan dispatchPlan;

        try
        {
            dispatchPlan = await ResolvePilotDispatchPlanAsync(
                bot.OwnerUserId,
                exchangeAccount.Id,
                signal,
                symbolMetadata,
                marketState.ReferencePrice.Value,
                cancellationToken);
        }
        catch (ExecutionValidationException exception)
        {
            if (signal.SignalType == StrategySignalType.Exit &&
                (string.Equals(exception.ReasonCode, "ReduceOnlyWithoutOpenPosition", StringComparison.Ordinal) ||
                 string.Equals(exception.ReasonCode, "ReduceOnlyQuantityInvalid", StringComparison.Ordinal)))
            {
                var decisionReasonCode = string.Equals(exception.ReasonCode, "ReduceOnlyQuantityInvalid", StringComparison.Ordinal)
                    ? NoClosableQuantityForExitDecisionCode
                    : NoOpenPositionForExitDecisionCode;
                var decisionSummary = string.Equals(exception.ReasonCode, "ReduceOnlyQuantityInvalid", StringComparison.Ordinal)
                    ? $"Exit signal was skipped because no closable reduce-only quantity could be resolved for {signal.Symbol} on the selected exchange account."
                    : $"Exit signal was skipped because no open position exists for {signal.Symbol} on the selected exchange account.";

                await WriteExitSkippedDecisionTraceAsync(
                    bot.OwnerUserId,
                    publishedVersion,
                    signal,
                    correlationId,
                    strategyDecisionTrace,
                    decisionReasonCode,
                    decisionSummary,
                    currentNetQuantity,
                    cancellationToken);

                logger.LogInformation(
                    exception,
                    "Bot execution pilot skipped exit dispatch for BotId {BotId} because dispatch planning resolved no closable position for {Symbol}. StrategySignalId={StrategySignalId} ReasonCode={ReasonCode}.",
                    bot.Id,
                    normalizedSymbol,
                    signal.StrategySignalId,
                    decisionReasonCode);
                return BackgroundJobProcessResult.Success();
            }

            logger.LogWarning(
                exception,
                "Bot execution pilot rejected BotId {BotId} because dispatch planning failed for {Symbol}. SignalType={SignalType}.",
                bot.Id,
                normalizedSymbol,
                signal.SignalType);
            return BackgroundJobProcessResult.PermanentFailure(
                ResolveStableFailureCode(exception.ReasonCode, submittedToBroker: false));
        }

        if (dispatchPlan.Quantity <= 0m)
        {
            if (signal.SignalType == StrategySignalType.Exit)
            {
                await WriteExitSkippedDecisionTraceAsync(
                    bot.OwnerUserId,
                    publishedVersion,
                    signal,
                    correlationId,
                    strategyDecisionTrace,
                    NoClosableQuantityForExitDecisionCode,
                    $"Exit signal was skipped because no closable reduce-only quantity could be resolved for {signal.Symbol} on the selected exchange account.",
                    currentNetQuantity,
                    cancellationToken);
            }

            logger.LogInformation(
                "Bot execution pilot skipped BotId {BotId} because actionable signal {SignalType} did not have executable quantity for {Symbol}.",
                bot.Id,
                signal.SignalType,
                normalizedSymbol);
            return BackgroundJobProcessResult.Success();
        }

        var pilotExecutionContext = BuildPilotExecutionContext(marginType!, leverage!.Value, pilotActivationEnabled);
        var preSubmitPilotEvaluation = await userExecutionOverrideGuard.EvaluateAsync(
            new UserExecutionOverrideEvaluationRequest(
                bot.OwnerUserId,
                signal.Symbol,
                ExecutionEnvironment.Live,
                dispatchPlan.Side,
                dispatchPlan.Quantity,
                marketState.ReferencePrice.Value,
                bot.Id,
                bot.StrategyKey,
                pilotExecutionContext,
                signal.TradingStrategyId,
                signal.TradingStrategyVersionId,
                signal.Timeframe,
                CurrentExecutionOrderId: null,
                ReplacesExecutionOrderId: null,
                ExchangeDataPlane.Futures),
            cancellationToken);

        if (IsPilotPreSubmitNotionalBlock(preSubmitPilotEvaluation))
        {
            logger.LogWarning(
                "Bot execution pilot rejected BotId {BotId} before execution dispatch because pilot order notional guard blocked the request. Symbol={Symbol} SignalType={SignalType} Side={Side} Quantity={Quantity} Price={Price} BlockCode={BlockCode}",
                bot.Id,
                signal.Symbol,
                signal.SignalType,
                dispatchPlan.Side,
                dispatchPlan.Quantity,
                marketState.ReferencePrice.Value,
                preSubmitPilotEvaluation.BlockCode ?? "UserExecutionPilotNotionalHardCapExceeded");
            return BackgroundJobProcessResult.PermanentFailure(
                preSubmitPilotEvaluation.BlockCode ?? "UserExecutionPilotNotionalHardCapExceeded");
        }

        try
        {
            var dispatchResult = await executionEngine.DispatchAsync(
                new ExecutionCommand(
                    Actor: "system:bot-worker",
                    OwnerUserId: bot.OwnerUserId,
                    TradingStrategyId: signal.TradingStrategyId,
                    TradingStrategyVersionId: signal.TradingStrategyVersionId,
                    StrategySignalId: signal.StrategySignalId,
                    SignalType: signal.SignalType,
                    StrategyKey: bot.StrategyKey,
                    Symbol: signal.Symbol,
                    Timeframe: signal.Timeframe,
                    BaseAsset: symbolMetadata.BaseAsset,
                    QuoteAsset: symbolMetadata.QuoteAsset,
                    Side: dispatchPlan.Side,
                    OrderType: ExecutionOrderType.Market,
                    Quantity: dispatchPlan.Quantity,
                    Price: marketState.ReferencePrice.Value,
                    BotId: bot.Id,
                    ExchangeAccountId: exchangeAccount.Id,
                    IsDemo: false,
                    IdempotencyKey: $"{idempotencyKey}:{signal.StrategySignalId:N}",
                    CorrelationId: null,
                    ParentCorrelationId: null,
                    Context: pilotExecutionContext,
                    ReduceOnly: dispatchPlan.ReduceOnly),
                cancellationToken);

            logger.LogInformation(
                "Bot execution pilot dispatch completed. BotId={BotId} StrategySignalId={StrategySignalId} ExecutionOrderId={ExecutionOrderId} State={State} Executor={ExecutorKind} Quantity={Quantity} Symbol={Symbol} FailureCode={FailureCode}.",
                bot.Id,
                signal.StrategySignalId,
                dispatchResult.Order.ExecutionOrderId,
                dispatchResult.Order.State,
                dispatchResult.Order.ExecutorKind,
                dispatchResult.Order.Quantity,
                dispatchResult.Order.Symbol,
                dispatchResult.Order.FailureCode ?? "none");

            return MapDispatchResult(dispatchResult);
        }
        catch (ExecutionValidationException exception)
        {
            logger.LogWarning(
                exception,
                "Bot execution pilot rejected BotId {BotId} because execution validation failed for {Symbol}.",
                bot.Id,
                normalizedSymbol);
            return BackgroundJobProcessResult.PermanentFailure(
                ResolveStableFailureCode(exception.ReasonCode, submittedToBroker: false));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                exception,
                "Bot execution pilot failed while dispatching an execution command for BotId {BotId}.",
                bot.Id);
            return BackgroundJobProcessResult.RetryableFailure("ExecutionDispatchFailed");
        }
    }

    private async Task<ExchangeAccount?> ResolveExchangeAccountAsync(TradingBot bot, CancellationToken cancellationToken)
    {
        if (bot.ExchangeAccountId.HasValue)
        {
            return await dbContext.ExchangeAccounts
                .IgnoreQueryFilters()
                .SingleOrDefaultAsync(
                    entity => entity.Id == bot.ExchangeAccountId.Value &&
                              entity.OwnerUserId == bot.OwnerUserId &&
                              !entity.IsDeleted &&
                              entity.CredentialStatus == ExchangeCredentialStatus.Active &&
                              !entity.IsReadOnly &&
                              entity.ExchangeName == "Binance",
                    cancellationToken);
        }

        var exchangeAccounts = await dbContext.ExchangeAccounts
            .IgnoreQueryFilters()
            .Where(entity =>
                entity.OwnerUserId == bot.OwnerUserId &&
                !entity.IsDeleted &&
                entity.CredentialStatus == ExchangeCredentialStatus.Active &&
                !entity.IsReadOnly &&
                entity.ExchangeName == "Binance")
            .OrderBy(entity => entity.Id)
            .ToListAsync(cancellationToken);

        return exchangeAccounts.Count == 1
            ? exchangeAccounts[0]
            : null;
    }

    private async Task<TradingStrategyVersion?> ResolvePublishedStrategyVersionAsync(
        TradingBot bot,
        CancellationToken cancellationToken)
    {
        var strategy = await dbContext.TradingStrategies
            .IgnoreQueryFilters()
            .Where(entity => entity.OwnerUserId == bot.OwnerUserId &&
                             entity.StrategyKey == bot.StrategyKey &&
                             !entity.IsDeleted)
            .OrderByDescending(entity => entity.UpdatedDate)
            .ThenByDescending(entity => entity.CreatedDate)
            .FirstOrDefaultAsync(cancellationToken);

        if (strategy is null)
        {
            return null;
        }

        return await StrategyRuntimeVersionSelection.ResolveAsync(
            dbContext,
            strategy.Id,
            cancellationToken);
    }

    private async Task<SymbolMetadataSnapshot?> ResolveSymbolMetadataAsync(
        string symbol,
        CancellationToken cancellationToken)
    {
        var cachedMetadata = await marketDataService.GetSymbolMetadataAsync(symbol, cancellationToken);

        if (cachedMetadata is not null)
        {
            return cachedMetadata;
        }

        var snapshots = await exchangeInfoClient.GetSymbolMetadataAsync([symbol], cancellationToken);
        return snapshots.SingleOrDefault();
    }

    private async Task<MarketStateResolution> ResolveMarketStateAsync(
        string symbol,
        string timeframe,
        CancellationToken cancellationToken)
    {
        var latestPrice = await marketDataService.GetLatestPriceAsync(symbol, cancellationToken);
        var indicatorSnapshot = await indicatorDataService.GetLatestAsync(symbol, timeframe, cancellationToken);

        if (indicatorSnapshot is not null &&
            indicatorSnapshot.State == IndicatorDataState.Ready &&
            latestPrice is not null)
        {
            return new MarketStateResolution(indicatorSnapshot, latestPrice.Price, Array.Empty<MarketCandleSnapshot>());
        }

        IReadOnlyCollection<MarketCandleSnapshot> historicalCandles;

        try
        {
            var interval = ResolveIntervalDuration(timeframe);
            var currentOpenTimeUtc = AlignToIntervalBoundary(timeProvider.GetUtcNow().UtcDateTime, timeframe);
            var lastClosedOpenTimeUtc = currentOpenTimeUtc - interval;
            var startOpenTimeUtc = lastClosedOpenTimeUtc - ((optionsValue.PrimeHistoricalCandleCount - 1) * interval);

            historicalCandles = await historicalKlineClient.GetClosedCandlesAsync(
                symbol,
                timeframe,
                startOpenTimeUtc,
                lastClosedOpenTimeUtc,
                optionsValue.PrimeHistoricalCandleCount,
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                exception,
                "Bot execution pilot failed while backfilling historical candles for {Symbol} {Timeframe}.",
                symbol,
                timeframe);
            return new MarketStateResolution(indicatorSnapshot, latestPrice?.Price, Array.Empty<MarketCandleSnapshot>());
        }

        if (historicalCandles.Count == 0)
        {
            return new MarketStateResolution(indicatorSnapshot, latestPrice?.Price, Array.Empty<MarketCandleSnapshot>());
        }

        indicatorSnapshot = await indicatorDataService.PrimeAsync(
            symbol,
            timeframe,
            historicalCandles,
            cancellationToken);
        if (marketDataService is MarketDataService concreteMarketDataService)
        {
            await PrimeHistoricalMarketDataCacheAsync(
                concreteMarketDataService,
                historicalCandles,
                cancellationToken);
        }


        await RecordHistoricalMarketDataHeartbeatAsync(
            symbol,
            timeframe,
            historicalCandles,
            cancellationToken);

        var historicalReferencePrice = historicalCandles
            .OrderByDescending(snapshot => snapshot.CloseTimeUtc)
            .Select(snapshot => (decimal?)snapshot.ClosePrice)
            .FirstOrDefault();

        return new MarketStateResolution(
            indicatorSnapshot is not null && indicatorSnapshot.State == IndicatorDataState.Ready
                ? indicatorSnapshot
                : null,
            latestPrice?.Price ?? historicalReferencePrice,
            historicalCandles);
    }


    private async Task PrimeHistoricalMarketDataCacheAsync(
        MarketDataService concreteMarketDataService,
        IReadOnlyCollection<MarketCandleSnapshot> historicalCandles,
        CancellationToken cancellationToken)
    {
        var latestSnapshot = historicalCandles
            .Where(snapshot => snapshot.IsClosed)
            .OrderByDescending(snapshot => snapshot.CloseTimeUtc)
            .FirstOrDefault();

        if (latestSnapshot is null)
        {
            return;
        }

        var klineProjectionResult = await concreteMarketDataService.RecordKlineAsync(
            latestSnapshot,
            guardResult: null,
            cancellationToken);

        if (klineProjectionResult.Status is not SharedMarketDataProjectionStatus.Accepted and not SharedMarketDataProjectionStatus.IgnoredOutOfOrder)
        {
            logger.LogWarning(
                "Bot execution pilot historical kline cache projection rejected {Symbol} {Timeframe} with {Status} ({ReasonCode}).",
                latestSnapshot.Symbol,
                latestSnapshot.Interval,
                klineProjectionResult.Status,
                klineProjectionResult.ReasonCode);
            return;
        }

        var tickerProjectionResult = await concreteMarketDataService.RecordPriceAsync(
            new MarketPriceSnapshot(
                latestSnapshot.Symbol,
                latestSnapshot.ClosePrice,
                latestSnapshot.CloseTimeUtc,
                latestSnapshot.ReceivedAtUtc,
                latestSnapshot.Source),
            cancellationToken);

        if (tickerProjectionResult.Status is SharedMarketDataProjectionStatus.Accepted or SharedMarketDataProjectionStatus.IgnoredOutOfOrder)
        {
            return;
        }

        logger.LogWarning(
            "Bot execution pilot historical ticker cache projection rejected {Symbol} with {Status} ({ReasonCode}).",
            latestSnapshot.Symbol,
            tickerProjectionResult.Status,
            tickerProjectionResult.ReasonCode);
    }


    private async Task<TradingFeatureSnapshotModel?> TryCaptureFeatureSnapshotAsync(
        TradingBot bot,
        ExchangeAccount exchangeAccount,
        string symbol,
        string timeframe,
        MarketStateResolution marketState,
        CancellationToken cancellationToken)
    {
        try
        {
            return await featureSnapshotService.CaptureAsync(
                new TradingFeatureCaptureRequest(
                    bot.OwnerUserId,
                    bot.Id,
                    bot.StrategyKey,
                    symbol,
                    timeframe,
                    timeProvider.GetUtcNow().UtcDateTime,
                    exchangeAccount.Id,
                    ExchangeDataPlane.Futures,
                    marketState.IndicatorSnapshot,
                    marketState.ReferencePrice,
                    marketState.HistoricalCandles),
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                exception,
                "Bot execution pilot failed while capturing a trading feature snapshot for BotId {BotId}.",
                bot.Id);
            return null;
        }
    }

    private async Task RecordHistoricalMarketDataHeartbeatAsync(
        string symbol,
        string timeframe,
        IReadOnlyCollection<MarketCandleSnapshot> historicalCandles,
        CancellationToken cancellationToken)
    {
        if (historicalCandles.Count == 0)
        {
            return;
        }

        var interval = ResolveIntervalDuration(timeframe);
        var orderedCandles = historicalCandles
            .Where(snapshot => snapshot.IsClosed)
            .OrderBy(snapshot => snapshot.OpenTimeUtc)
            .ToArray();

        if (orderedCandles.Length == 0)
        {
            return;
        }

        var latestSnapshot = orderedCandles[^1];
        var continuityGapCount = CountContinuityGaps(orderedCandles, interval);
        var guardStateCode = continuityGapCount == 0
            ? DegradedModeStateCode.Normal
            : DegradedModeStateCode.Stopped;
        var guardReasonCode = continuityGapCount == 0
            ? DegradedModeReasonCode.None
            : DegradedModeReasonCode.CandleDataGapDetected;

        await dataLatencyCircuitBreaker.RecordHeartbeatAsync(
            new DataLatencyHeartbeat(
                Source: "binance:rest-backfill",
                DataTimestampUtc: latestSnapshot.CloseTimeUtc,
                GuardStateCode: guardStateCode,
                GuardReasonCode: guardReasonCode,
                Symbol: symbol,
                Timeframe: timeframe,
                ExpectedOpenTimeUtc: latestSnapshot.OpenTimeUtc + interval,
                ContinuityGapCount: continuityGapCount,
                HeartbeatReceivedAtUtc: latestSnapshot.ReceivedAtUtc),
            cancellationToken: cancellationToken);
    }

    private static int CountContinuityGaps(
        IReadOnlyList<MarketCandleSnapshot> orderedCandles,
        TimeSpan interval)
    {
        if (orderedCandles.Count < 2)
        {
            return 0;
        }

        var continuityGapCount = 0;
        var previousOpenTimeUtc = NormalizeTimestamp(orderedCandles[0].OpenTimeUtc);

        for (var index = 1; index < orderedCandles.Count; index++)
        {
            var currentOpenTimeUtc = NormalizeTimestamp(orderedCandles[index].OpenTimeUtc);

            if (currentOpenTimeUtc <= previousOpenTimeUtc)
            {
                continue;
            }

            var expectedOpenTimeUtc = previousOpenTimeUtc + interval;

            if (currentOpenTimeUtc > expectedOpenTimeUtc)
            {
                continuityGapCount += Math.Max(
                    1,
                    (int)Math.Round(
                        (currentOpenTimeUtc - expectedOpenTimeUtc).TotalMilliseconds / interval.TotalMilliseconds,
                        MidpointRounding.AwayFromZero));
            }

            previousOpenTimeUtc = currentOpenTimeUtc;
        }

        return continuityGapCount;
    }

    private async Task<StrategySignalSnapshot?> ResolveActionableSignalAsync(
        StrategySignalGenerationResult generationResult,
        Guid tradingStrategyVersionId,
        string symbol,
        string timeframe,
        DateTime indicatorCloseTimeUtc,
        bool preferExitSignal,
        CancellationToken cancellationToken)
    {
        var resolvedSignal = SelectLatestActionableSignal(
            generationResult.Signals,
            symbol,
            timeframe,
            preferExitSignal);

        if (resolvedSignal is not null)
        {
            return resolvedSignal;
        }

        if (generationResult.SuppressedDuplicateCount == 0)
        {
            return null;
        }

        foreach (var signalType in EnumeratePreferredSignalTypes(preferExitSignal))
        {
            var persistedSignal = await dbContext.TradingStrategySignals
                .IgnoreQueryFilters()
                .Where(entity =>
                    entity.TradingStrategyVersionId == tradingStrategyVersionId &&
                    entity.SignalType == signalType &&
                    entity.Symbol == symbol &&
                    entity.Timeframe == timeframe &&
                    entity.IndicatorCloseTimeUtc == indicatorCloseTimeUtc &&
                    !entity.IsDeleted)
                .OrderByDescending(entity => entity.GeneratedAtUtc)
                .FirstOrDefaultAsync(cancellationToken);

            if (persistedSignal is not null)
            {
                return await strategySignalService.GetAsync(persistedSignal.Id, cancellationToken);
            }
        }

        return null;
    }

    private async Task<PilotDispatchPlan> ResolvePilotDispatchPlanAsync(
        string ownerUserId,
        Guid exchangeAccountId,
        StrategySignalSnapshot signal,
        SymbolMetadataSnapshot symbolMetadata,
        decimal referencePrice,
        CancellationToken cancellationToken)
    {
        if (signal.SignalType == StrategySignalType.Entry)
        {
            return new PilotDispatchPlan(
                ExecutionOrderSide.Buy,
                ResolvePilotQuantity(symbolMetadata, referencePrice),
                ReduceOnly: false);
        }

        var currentNetQuantity = await ResolveCurrentNetQuantityAsync(
            ownerUserId,
            exchangeAccountId,
            signal.Symbol,
            cancellationToken);

        if (currentNetQuantity == 0m)
        {
            throw new ExecutionValidationException(
                "ReduceOnlyWithoutOpenPosition",
                $"Execution blocked because exit signal requires an open position for {signal.Symbol}.");
        }

        return new PilotDispatchPlan(
            currentNetQuantity > 0m ? ExecutionOrderSide.Sell : ExecutionOrderSide.Buy,
            ResolvePilotExitQuantity(symbolMetadata, Math.Abs(currentNetQuantity)),
            ReduceOnly: true);
    }

    private async Task<decimal> ResolveCurrentNetQuantityAsync(
        string ownerUserId,
        Guid exchangeAccountId,
        string symbol,
        CancellationToken cancellationToken)
    {
        var normalizedSymbol = NormalizePositionSymbol(symbol);

        var positionNetQuantity = (await dbContext.ExchangePositions
                .AsNoTracking()
                .IgnoreQueryFilters()
                .Where(entity =>
                    entity.OwnerUserId == ownerUserId &&
                    entity.ExchangeAccountId == exchangeAccountId &&
                    entity.Plane == ExchangeDataPlane.Futures &&
                    !entity.IsDeleted)
                .ToListAsync(cancellationToken))
            .Where(entity => NormalizePositionSymbol(entity.Symbol) == normalizedSymbol)
            .Sum(ResolveSignedPositionQuantity);

        if (positionNetQuantity != 0m)
        {
            return positionNetQuantity;
        }

        return (await dbContext.ExecutionOrders
                .AsNoTracking()
                .IgnoreQueryFilters()
                .Where(entity =>
                    entity.OwnerUserId == ownerUserId &&
                    entity.ExchangeAccountId == exchangeAccountId &&
                    entity.Plane == ExchangeDataPlane.Futures &&
                    !entity.IsDeleted &&
                    entity.SubmittedToBroker &&
                    (entity.State == ExecutionOrderState.Submitted ||
                     entity.State == ExecutionOrderState.Dispatching ||
                     entity.State == ExecutionOrderState.CancelRequested ||
                     entity.State == ExecutionOrderState.PartiallyFilled ||
                     entity.State == ExecutionOrderState.Filled))
                .ToListAsync(cancellationToken))
            .Where(entity => NormalizePositionSymbol(entity.Symbol) == normalizedSymbol)
            .Sum(ResolveSignedOrderQuantity);
    }

    private static decimal ResolveSignedOrderQuantity(ExecutionOrder entity)
    {
        var quantity = entity.FilledQuantity > 0m
            ? entity.FilledQuantity
            : ResolvePendingExposureQuantity(entity);
        if (quantity == 0m)
        {
            return 0m;
        }

        return entity.Side == ExecutionOrderSide.Buy
            ? quantity
            : -quantity;
    }

    private static decimal ResolvePendingExposureQuantity(ExecutionOrder entity)
    {
        if (!entity.SubmittedToBroker ||
            entity.ReduceOnly ||
            entity.OrderType != ExecutionOrderType.Market)
        {
            return 0m;
        }

        return entity.State is ExecutionOrderState.Submitted or
            ExecutionOrderState.Dispatching or
            ExecutionOrderState.CancelRequested
            ? entity.Quantity
            : 0m;
    }

    private static decimal ResolveSignedPositionQuantity(ExchangePosition entity)
    {
        var quantity = entity.Quantity;
        if (quantity == 0m)
        {
            return 0m;
        }

        return NormalizePositionSide(entity.PositionSide) == "SHORT"
            ? -Math.Abs(quantity)
            : quantity;
    }

    private static string NormalizePositionSide(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? "BOTH"
            : value.Trim().ToUpperInvariant();
    }

    private static string NormalizePositionSymbol(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().ToUpperInvariant();
    }

    private bool TryResolvePilotExecutionParameters(
        TradingBot bot,
        out decimal? leverage,
        out string? marginType,
        out string? failureCode)
    {
        leverage = bot.Leverage ?? 1m;
        marginType = string.IsNullOrWhiteSpace(bot.MarginType)
            ? "ISOLATED"
            : bot.MarginType.Trim().ToUpperInvariant();
        failureCode = null;

        if (leverage != 1m && !IsClockDriftSmokeLeverageAllowed(bot, leverage))
        {
            failureCode = "PilotLeverageMustBeOne";
            return false;
        }

        if (!string.Equals(marginType, "ISOLATED", StringComparison.Ordinal))
        {
            failureCode = "PilotMarginTypeMustBeIsolated";
            return false;
        }

        return true;
    }

    private static StrategySignalSnapshot? SelectLatestActionableSignal(
        IReadOnlyCollection<StrategySignalSnapshot> signals,
        string symbol,
        string timeframe,
        bool preferExitSignal)
    {
        var matchingSignals = signals
            .Where(signal =>
                signal.Symbol == symbol &&
                signal.Timeframe == timeframe)
            .OrderByDescending(signal => signal.GeneratedAtUtc)
            .ToArray();

        foreach (var signalType in EnumeratePreferredSignalTypes(preferExitSignal))
        {
            var candidate = matchingSignals.FirstOrDefault(signal => signal.SignalType == signalType);
            if (candidate is not null)
            {
                return candidate;
            }
        }

        return null;
    }

    private static IEnumerable<StrategySignalType> EnumeratePreferredSignalTypes(bool preferExitSignal)
    {
        if (preferExitSignal)
        {
            yield return StrategySignalType.Exit;
            yield return StrategySignalType.Entry;
            yield break;
        }

        yield return StrategySignalType.Entry;
        yield return StrategySignalType.Exit;
    }

    private static decimal ResolvePilotQuantity(
        SymbolMetadataSnapshot symbolMetadata,
        decimal referencePrice)
    {
        var candidateQuantity = symbolMetadata.MinQuantity
            ?? symbolMetadata.StepSize;

        if (candidateQuantity <= 0m)
        {
            throw new ExecutionValidationException($"Pilot quantity could not be resolved for '{symbolMetadata.Symbol}'.");
        }

        if (symbolMetadata.MinNotional is decimal minNotional)
        {
            candidateQuantity = Math.Max(candidateQuantity, minNotional / referencePrice);
        }

        candidateQuantity = AlignUp(candidateQuantity, symbolMetadata.StepSize);

        if (symbolMetadata.MinQuantity is decimal minQuantity && candidateQuantity < minQuantity)
        {
            candidateQuantity = AlignUp(minQuantity, symbolMetadata.StepSize);
        }

        if (symbolMetadata.QuantityPrecision is int quantityPrecision)
        {
            candidateQuantity = decimal.Round(candidateQuantity, quantityPrecision, MidpointRounding.AwayFromZero);
        }

        if (symbolMetadata.MinNotional is decimal adjustedMinNotional &&
            (candidateQuantity * referencePrice) < adjustedMinNotional)
        {
            candidateQuantity = AlignUp(adjustedMinNotional / referencePrice, symbolMetadata.StepSize);
        }

        if (candidateQuantity <= 0m)
        {
            throw new ExecutionValidationException($"Pilot quantity resolved to a non-positive value for '{symbolMetadata.Symbol}'.");
        }

        return candidateQuantity;
    }

    private static bool IsPilotPreSubmitNotionalBlock(UserExecutionOverrideEvaluationResult evaluationResult)
    {
        if (!evaluationResult.IsBlocked)
        {
            return false;
        }

        if (IsPilotPreSubmitNotionalBlockCode(evaluationResult.BlockCode))
        {
            return true;
        }

        if (evaluationResult.BlockReasons is null)
        {
            return false;
        }

        foreach (var blockedReason in evaluationResult.BlockReasons)
        {
            if (IsPilotPreSubmitNotionalBlockCode(blockedReason))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsPilotPreSubmitNotionalBlockCode(string? blockCode)
    {
        return string.Equals(blockCode, "UserExecutionPilotNotionalHardCapExceeded", StringComparison.Ordinal) ||
               string.Equals(blockCode, "UserExecutionPilotNotionalConfigurationMissing", StringComparison.Ordinal) ||
               string.Equals(blockCode, "UserExecutionPilotNotionalConfigurationInvalid", StringComparison.Ordinal) ||
               string.Equals(blockCode, "UserExecutionPilotNotionalDataUnavailable", StringComparison.Ordinal);
    }

    private static BackgroundJobProcessResult MapDispatchResult(ExecutionDispatchResult dispatchResult)
    {
        return dispatchResult.Order.State switch
        {
            ExecutionOrderState.Submitted or
            ExecutionOrderState.PartiallyFilled or
            ExecutionOrderState.Filled => BackgroundJobProcessResult.Success(),
            ExecutionOrderState.Rejected => BackgroundJobProcessResult.PermanentFailure(
                dispatchResult.Order.FailureCode ?? "ExecutionRejected"),
            ExecutionOrderState.Failed when string.Equals(
                dispatchResult.Order.FailureCode,
                "ExecutionValidationException",
                StringComparison.Ordinal) => BackgroundJobProcessResult.PermanentFailure(
                    ResolveStableFailureCode(dispatchResult.Order.FailureCode, submittedToBroker: false)),
            ExecutionOrderState.Failed => BackgroundJobProcessResult.RetryableFailure(
                dispatchResult.Order.FailureCode ?? "ExecutionFailed"),
            _ => BackgroundJobProcessResult.RetryableFailure("UnexpectedExecutionState")
        };
    }

    private static string ResolveStableFailureCode(string? failureCode, bool submittedToBroker)
    {
        var normalizedFailureCode = failureCode?.Trim();

        if (string.IsNullOrWhiteSpace(normalizedFailureCode) ||
            string.Equals(normalizedFailureCode, "ExecutionValidationException", StringComparison.Ordinal))
        {
            return submittedToBroker
                ? "DispatchFailed"
                : "PreSubmitFailed";
        }

        return normalizedFailureCode;
    }

    private async Task<AiShadowDecisionSnapshot> CaptureShadowDecisionAsync(
        TradingBot bot,
        ExchangeAccount exchangeAccount,
        TradingStrategyVersion publishedVersion,
        string symbol,
        string timeframe,
        SymbolMetadataSnapshot symbolMetadata,
        MarketStateResolution marketState,
        TradingFeatureSnapshotModel? featureSnapshot,
        StrategySignalGenerationResult signalGenerationResult,
        StrategySignalSnapshot? signal,
        DecisionTrace? strategyDecisionTrace,
        string correlationId,
        string marginType,
        decimal leverage,
        CancellationToken cancellationToken)
    {
        var shadowDecisionId = Guid.NewGuid();
        var strategyDirection = ResolveStrategyDirection(signalGenerationResult.EvaluationResult);
        var aiEvaluation = ResolvePrimaryAiEvaluation(signalGenerationResult);
        var duplicateSuppressed = signalGenerationResult.SuppressedDuplicateCount > 0 && signalGenerationResult.Signals.Count == 0;
        var hypotheticalEvaluation = await EvaluateHypotheticalSubmitAsync(
            shadowDecisionId,
            bot,
            exchangeAccount,
            publishedVersion,
            symbol,
            timeframe,
            symbolMetadata,
            marketState,
            correlationId,
            marginType,
            leverage,
            shouldEvaluate: string.Equals(strategyDirection, "Long", StringComparison.Ordinal) && !duplicateSuppressed,
            cancellationToken);
        var persistedRiskVeto = ResolvePersistedRiskVeto(signalGenerationResult);
        var riskVetoPresent = hypotheticalEvaluation.RiskVetoPresent || persistedRiskVeto.IsPresent;
        var riskVetoReason = hypotheticalEvaluation.RiskVetoReason ?? persistedRiskVeto.Reason;
        var riskVetoSummary = hypotheticalEvaluation.RiskVetoSummary ?? persistedRiskVeto.Summary;
        var finalAction = signal is not null &&
                          !duplicateSuppressed &&
                          hypotheticalEvaluation.SubmitAllowed
            ? "ShadowOnly"
            : "NoSubmit";
        var noSubmitReason = string.Equals(finalAction, "ShadowOnly", StringComparison.Ordinal)
            ? "ShadowModeActive"
            : ResolveShadowNoSubmitReason(
                strategyDecisionTrace,
                hypotheticalEvaluation.BlockReason,
                duplicateSuppressed,
                strategyDirection,
                aiEvaluation);

        return await aiShadowDecisionService.CaptureAsync(
            new AiShadowDecisionWriteRequest(
                shadowDecisionId,
                bot.OwnerUserId,
                bot.Id,
                exchangeAccount.Id,
                publishedVersion.TradingStrategyId,
                publishedVersion.Id,
                signal?.StrategySignalId,
                signalGenerationResult.Vetoes.OrderByDescending(item => item.EvaluatedAtUtc).Select(item => (Guid?)item.StrategySignalVetoId).FirstOrDefault(),
                featureSnapshot?.Id,
                strategyDecisionTrace?.Id,
                hypotheticalEvaluation.DecisionTraceId,
                correlationId,
                bot.StrategyKey,
                symbol,
                timeframe,
                timeProvider.GetUtcNow().UtcDateTime,
                featureSnapshot?.MarketDataTimestampUtc ?? marketState.IndicatorSnapshot?.CloseTimeUtc,
                featureSnapshot?.FeatureVersion,
                strategyDirection,
                ResolveStrategyConfidenceScore(signalGenerationResult, signal),
                strategyDecisionTrace?.DecisionOutcome,
                strategyDecisionTrace?.DecisionReasonCode ?? strategyDecisionTrace?.VetoReasonCode,
                strategyDecisionTrace?.DecisionSummary ?? signalGenerationResult.EvaluationReport?.ExplainabilitySummary,
                aiEvaluation?.SignalDirection.ToString() ?? "Neutral",
                aiEvaluation?.ConfidenceScore ?? 0m,
                ResolveAiReasonSummary(aiEvaluation, signalGenerationResult.EvaluationResult),
                aiEvaluation?.ProviderName ?? ResolveConfiguredAiProviderName(),
                aiEvaluation?.ProviderModel ?? ResolveConfiguredAiProviderModel(),
                aiEvaluation?.LatencyMs ?? 0,
                aiEvaluation?.IsFallback ?? false,
                aiEvaluation?.FallbackReason?.ToString(),
                riskVetoPresent,
                riskVetoReason,
                riskVetoSummary,
                hypotheticalEvaluation.PilotSafetyBlocked,
                hypotheticalEvaluation.PilotSafetyReason,
                hypotheticalEvaluation.PilotSafetySummary,
                featureSnapshot?.TradingContext.TradingMode ?? optionsValue.SignalEvaluationMode,
                featureSnapshot?.TradingContext.Plane ?? ExchangeDataPlane.Futures,
                finalAction,
                hypotheticalEvaluation.SubmitAllowed,
                hypotheticalEvaluation.BlockReason,
                hypotheticalEvaluation.BlockSummary,
                noSubmitReason,
                featureSnapshot?.FeatureSummary,
                ResolveAgreementState(strategyDirection, aiEvaluation)),
            cancellationToken);
    }

    private async Task<ShadowHypotheticalEvaluation> EvaluateHypotheticalSubmitAsync(
        Guid shadowDecisionId,
        TradingBot bot,
        ExchangeAccount exchangeAccount,
        TradingStrategyVersion publishedVersion,
        string symbol,
        string timeframe,
        SymbolMetadataSnapshot symbolMetadata,
        MarketStateResolution marketState,
        string correlationId,
        string marginType,
        decimal leverage,
        bool shouldEvaluate,
        CancellationToken cancellationToken)
    {
        if (!shouldEvaluate)
        {
            return ShadowHypotheticalEvaluation.NotEvaluated;
        }

        decimal quantity;

        try
        {
            quantity = ResolvePilotQuantity(symbolMetadata, marketState.ReferencePrice ?? 0m);
        }
        catch (ExecutionValidationException exception)
        {
            return new ShadowHypotheticalEvaluation(
                SubmitAllowed: false,
                BlockReason: ResolveStableFailureCode(exception.ReasonCode, submittedToBroker: false),
                BlockSummary: Truncate(exception.Message, 512),
                PilotSafetyBlocked: false,
                PilotSafetyReason: null,
                PilotSafetySummary: null,
                RiskVetoPresent: false,
                RiskVetoReason: null,
                RiskVetoSummary: null,
                DecisionTraceId: null);
        }

        var shadowContext = BuildShadowExecutionContext(marginType, leverage);
        DecisionTrace? hypotheticalDecisionTrace = null;

        try
        {
            await executionGate.EnsureExecutionAllowedAsync(
                new ExecutionGateRequest(
                    Actor: "system:ai-shadow",
                    Action: "AiShadow.HypotheticalSubmit",
                    Target: $"AiShadowDecision/{shadowDecisionId:N}",
                    Environment: optionsValue.SignalEvaluationMode,
                    Context: shadowContext,
                    CorrelationId: correlationId,
                    UserId: bot.OwnerUserId,
                    BotId: bot.Id,
                    StrategyKey: bot.StrategyKey,
                    Symbol: symbol,
                    Timeframe: timeframe,
                    ExchangeAccountId: exchangeAccount.Id,
                    Plane: ExchangeDataPlane.Futures),
                cancellationToken);

            hypotheticalDecisionTrace = await ResolveLatestDecisionTraceAsync(
                correlationId,
                bot.OwnerUserId,
                symbol,
                timeframe,
                cancellationToken);
        }
        catch (ExecutionGateRejectedException exception)
        {
            hypotheticalDecisionTrace = await ResolveLatestDecisionTraceAsync(
                correlationId,
                bot.OwnerUserId,
                symbol,
                timeframe,
                cancellationToken);
            var pilotSafetyBlocked = IsPilotSafetyBlockedReason(exception.Reason);
            var blockedSummary = Truncate(exception.Message, 512);

            return new ShadowHypotheticalEvaluation(
                SubmitAllowed: false,
                BlockReason: exception.Reason.ToString(),
                BlockSummary: blockedSummary,
                PilotSafetyBlocked: pilotSafetyBlocked,
                PilotSafetyReason: pilotSafetyBlocked ? exception.Reason.ToString() : null,
                PilotSafetySummary: pilotSafetyBlocked ? blockedSummary : null,
                RiskVetoPresent: false,
                RiskVetoReason: null,
                RiskVetoSummary: null,
                DecisionTraceId: hypotheticalDecisionTrace?.Id);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new ShadowHypotheticalEvaluation(
                SubmitAllowed: false,
                BlockReason: "HypotheticalGateEvaluationUnavailable",
                BlockSummary: Truncate(exception.Message, 512),
                PilotSafetyBlocked: false,
                PilotSafetyReason: null,
                PilotSafetySummary: null,
                RiskVetoPresent: false,
                RiskVetoReason: null,
                RiskVetoSummary: null,
                DecisionTraceId: null);
        }

        var overrideEvaluation = await userExecutionOverrideGuard.EvaluateAsync(
            new UserExecutionOverrideEvaluationRequest(
                bot.OwnerUserId,
                symbol,
                optionsValue.SignalEvaluationMode,
                ExecutionOrderSide.Buy,
                quantity,
                marketState.ReferencePrice ?? 0m,
                bot.Id,
                bot.StrategyKey,
                shadowContext,
                publishedVersion.TradingStrategyId,
                publishedVersion.Id,
                timeframe,
                CurrentExecutionOrderId: null,
                ReplacesExecutionOrderId: null,
                ExchangeDataPlane.Futures),
            cancellationToken);
        var riskVetoReason = NormalizeRiskVetoReason(overrideEvaluation.RiskEvaluation);
        var riskVetoSummary = NormalizeRiskVetoSummary(overrideEvaluation.RiskEvaluation);

        if (overrideEvaluation.IsBlocked)
        {
            var pilotSafetyBlocked = IsPilotSafetyBlockedCode(overrideEvaluation.BlockCode);
            var blockedSummary = Truncate(overrideEvaluation.Message, 512);

            return new ShadowHypotheticalEvaluation(
                SubmitAllowed: false,
                BlockReason: overrideEvaluation.BlockCode ?? "UserExecutionOverrideBlocked",
                BlockSummary: blockedSummary,
                PilotSafetyBlocked: pilotSafetyBlocked,
                PilotSafetyReason: pilotSafetyBlocked ? overrideEvaluation.BlockCode : null,
                PilotSafetySummary: pilotSafetyBlocked ? blockedSummary : null,
                RiskVetoPresent: overrideEvaluation.RiskEvaluation?.IsVetoed ?? false,
                RiskVetoReason: riskVetoReason,
                RiskVetoSummary: riskVetoSummary,
                DecisionTraceId: hypotheticalDecisionTrace?.Id);
        }

        return new ShadowHypotheticalEvaluation(
            SubmitAllowed: true,
            BlockReason: null,
            BlockSummary: null,
            PilotSafetyBlocked: false,
            PilotSafetyReason: null,
            PilotSafetySummary: null,
            RiskVetoPresent: overrideEvaluation.RiskEvaluation?.IsVetoed ?? false,
            RiskVetoReason: riskVetoReason,
            RiskVetoSummary: riskVetoSummary,
            DecisionTraceId: hypotheticalDecisionTrace?.Id);
    }


    private async Task WriteExitSkippedDecisionTraceAsync(
        string ownerUserId,
        TradingStrategyVersion publishedVersion,
        StrategySignalSnapshot signal,
        string correlationId,
        DecisionTrace? previousDecisionTrace,
        string decisionReasonCode,
        string decisionSummary,
        decimal netQuantity,
        CancellationToken cancellationToken)
    {
        var snapshotJson = JsonSerializer.Serialize(new
        {
            PreviousDecisionOutcome = previousDecisionTrace?.DecisionOutcome,
            PreviousDecisionReasonCode = previousDecisionTrace?.DecisionReasonCode,
            SignalType = signal.SignalType.ToString(),
            SignalId = signal.StrategySignalId,
            Symbol = signal.Symbol,
            Timeframe = signal.Timeframe,
            ExchangeEnvironment = signal.Environment.ToString(),
            NetQuantity = netQuantity
        });

        await traceService.WriteDecisionTraceAsync(
            new DecisionTraceWriteRequest(
                ownerUserId,
                signal.Symbol,
                signal.Timeframe,
                BuildStrategyVersionLabel(publishedVersion),
                signal.SignalType.ToString(),
                ExitSkippedDecisionOutcome,
                snapshotJson,
                0,
                CorrelationId: correlationId,
                StrategySignalId: signal.StrategySignalId,
                DecisionReasonType: ExitSkippedDecisionReasonType,
                DecisionReasonCode: decisionReasonCode,
                DecisionSummary: decisionSummary,
                DecisionAtUtc: timeProvider.GetUtcNow().UtcDateTime),
            cancellationToken);
    }

    private async Task<DecisionTrace?> ResolveLatestDecisionTraceAsync(
        string correlationId,
        string userId,
        string symbol,
        string timeframe,
        CancellationToken cancellationToken)
    {
        return await dbContext.DecisionTraces
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(entity => !entity.IsDeleted &&
                             entity.CorrelationId == correlationId &&
                             entity.UserId == userId &&
                             entity.Symbol == symbol &&
                             entity.Timeframe == timeframe)
            .OrderByDescending(entity => entity.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private static string BuildStrategyVersionLabel(TradingStrategyVersion version)
    {
        return $"StrategyVersion:{version.Id:N}:v{version.VersionNumber}:s{version.SchemaVersion}";
    }

    private static string ResolveStrategyDirection(StrategyEvaluationResult evaluationResult)
    {
        if (evaluationResult.HasEntryRules && evaluationResult.EntryMatched)
        {
            return "Long";
        }

        return "Neutral";
    }

    private static int? ResolveStrategyConfidenceScore(
        StrategySignalGenerationResult signalGenerationResult,
        StrategySignalSnapshot? signal)
    {
        if (signal is not null)
        {
            return signal.ExplainabilityPayload.ConfidenceSnapshot.ScorePercentage;
        }

        var vetoConfidence = signalGenerationResult.Vetoes
            .OrderByDescending(item => item.EvaluatedAtUtc)
            .Select(item => (int?)item.ConfidenceSnapshot.ScorePercentage)
            .FirstOrDefault();

        if (vetoConfidence.HasValue)
        {
            return vetoConfidence.Value;
        }

        return signalGenerationResult.EvaluationReport?.AggregateScore;
    }

    private static AiSignalEvaluationResult? ResolvePrimaryAiEvaluation(StrategySignalGenerationResult signalGenerationResult)
    {
        return signalGenerationResult.AiEvaluations
            .OrderByDescending(item => item.EvaluatedAtUtc)
            .ThenByDescending(item => item.ConfidenceScore)
            .FirstOrDefault();
    }

    private static (bool IsPresent, string? Reason, string? Summary) ResolvePersistedRiskVeto(StrategySignalGenerationResult signalGenerationResult)
    {
        var veto = signalGenerationResult.Vetoes
            .OrderByDescending(item => item.EvaluatedAtUtc)
            .FirstOrDefault();

        if (veto is null)
        {
            return (false, null, null);
        }

        var reason = veto.ConfidenceSnapshot.RiskReasonCode == RiskVetoReasonCode.None
            ? null
            : veto.ConfidenceSnapshot.RiskReasonCode.ToString();
        var summary = string.IsNullOrWhiteSpace(veto.ConfidenceSnapshot.Summary)
            ? veto.UiLog.Summary
            : veto.ConfidenceSnapshot.Summary;

        return (true, reason, summary);
    }

    private string ResolveConfiguredAiProviderName()
    {
        var configuredProvider = aiSignalOptionsValue.SelectedProvider?.Trim();
        return string.IsNullOrWhiteSpace(configuredProvider)
            ? "Unknown"
            : configuredProvider;
    }

    private string? ResolveConfiguredAiProviderModel()
    {
        var providerName = ResolveConfiguredAiProviderName();

        if (string.Equals(providerName, OpenAiSignalProviderAdapter.ProviderNameValue, StringComparison.OrdinalIgnoreCase))
        {
            return Truncate(aiSignalOptionsValue.OpenAiModel, 128);
        }

        if (string.Equals(providerName, GeminiAiSignalProviderAdapter.ProviderNameValue, StringComparison.OrdinalIgnoreCase))
        {
            return Truncate(aiSignalOptionsValue.GeminiModel, 128);
        }

        return null;
    }

    private static string ResolveAiReasonSummary(
        AiSignalEvaluationResult? aiEvaluation,
        StrategyEvaluationResult evaluationResult)
    {
        if (aiEvaluation is not null)
        {
            return aiEvaluation.ReasonSummary;
        }

        return evaluationResult.HasEntryRules && evaluationResult.EntryMatched
            ? "AI evaluation was skipped because no AI overlay response was recorded."
            : "AI evaluation was skipped because strategy produced no entry candidate.";
    }

    private static string ResolveAgreementState(
        string strategyDirection,
        AiSignalEvaluationResult? aiEvaluation)
    {
        if (aiEvaluation is null)
        {
            return "NotApplicable";
        }

        return string.Equals(strategyDirection, aiEvaluation.SignalDirection.ToString(), StringComparison.Ordinal)
            ? "Agreement"
            : "Disagreement";
    }

    private static string ResolveShadowNoSubmitReason(
        DecisionTrace? strategyDecisionTrace,
        string? hypotheticalBlockReason,
        bool duplicateSuppressed,
        string strategyDirection,
        AiSignalEvaluationResult? aiEvaluation)
    {
        var normalizedHypotheticalBlockReason = hypotheticalBlockReason?.Trim();
        if (!string.IsNullOrWhiteSpace(normalizedHypotheticalBlockReason))
        {
            return normalizedHypotheticalBlockReason;
        }

        if (duplicateSuppressed)
        {
            return "SuppressedDuplicate";
        }

        var strategyReasonCode = strategyDecisionTrace?.DecisionReasonCode?.Trim();
        if (!string.IsNullOrWhiteSpace(strategyReasonCode))
        {
            return strategyReasonCode;
        }

        if (!string.Equals(strategyDirection, "Long", StringComparison.Ordinal))
        {
            return "NoActionableSignal";
        }

        if (aiEvaluation?.IsFallback == true)
        {
            return $"Ai{aiEvaluation.FallbackReason ?? AiSignalFallbackReason.EvaluationException}";
        }

        if (aiEvaluation is not null && aiEvaluation.SignalDirection == AiSignalDirection.Neutral)
        {
            return "AiNeutral";
        }

        return "NoSubmit";
    }

    private static string? NormalizeRiskVetoReason(RiskVetoResult? riskEvaluation)
    {
        if (riskEvaluation is null || riskEvaluation.ReasonCode == RiskVetoReasonCode.None)
        {
            return null;
        }

        return riskEvaluation.ReasonCode.ToString();
    }

    private static string? NormalizeRiskVetoSummary(RiskVetoResult? riskEvaluation)
    {
        if (riskEvaluation is null)
        {
            return null;
        }

        return Truncate(riskEvaluation.ReasonSummary, 1024);
    }


    private bool IsPilotHostAllowed()
    {
        return hostEnvironment.IsDevelopment() || optionsValue.AllowNonDevelopmentHost;
    }

    private bool IsClockDriftSmokeLeverageAllowed(TradingBot bot, decimal? leverage)
    {
        if (leverage == 1m)
        {
            return true;
        }

        return hostEnvironment.IsDevelopment() &&
               optionsValue.AllowNonOneLeverageForClockDriftSmoke;
    }

    private static bool IsPilotSafetyBlockedReason(ExecutionGateBlockedReason blockedReason)
    {
        return blockedReason is ExecutionGateBlockedReason.PilotConfigurationMissing or
            ExecutionGateBlockedReason.PilotRequiresDevelopment or
            ExecutionGateBlockedReason.PilotTestnetEndpointMismatch or
            ExecutionGateBlockedReason.PilotCredentialValidationUnavailable or
            ExecutionGateBlockedReason.PilotCredentialEnvironmentMismatch or
            ExecutionGateBlockedReason.PrivatePlaneUnavailable or
            ExecutionGateBlockedReason.PrivatePlaneStale;
    }

    private static bool IsPilotSafetyBlockedCode(string? blockCode)
    {
        if (string.IsNullOrWhiteSpace(blockCode))
        {
            return false;
        }

        return blockCode.StartsWith("UserExecutionPilot", StringComparison.Ordinal) ||
               string.Equals(blockCode, "UserExecutionBotCooldownActive", StringComparison.Ordinal) ||
               string.Equals(blockCode, "UserExecutionSymbolCooldownActive", StringComparison.Ordinal) ||
               string.Equals(blockCode, "UserExecutionMaxOpenPositionsExceeded", StringComparison.Ordinal) ||
               string.Equals(blockCode, "UserExecutionSymbolConflict", StringComparison.Ordinal) ||
               string.Equals(blockCode, "PilotConfigurationMissing", StringComparison.Ordinal) ||
               string.Equals(blockCode, "PilotRequiresDevelopment", StringComparison.Ordinal) ||
               string.Equals(blockCode, "PilotTestnetEndpointMismatch", StringComparison.Ordinal) ||
               string.Equals(blockCode, "PilotCredentialValidationUnavailable", StringComparison.Ordinal) ||
               string.Equals(blockCode, "PilotCredentialEnvironmentMismatch", StringComparison.Ordinal) ||
               string.Equals(blockCode, "PrivatePlaneUnavailable", StringComparison.Ordinal) ||
               string.Equals(blockCode, "PrivatePlaneStale", StringComparison.Ordinal);
    }

    private static string BuildShadowExecutionContext(string marginType, decimal leverage)
    {
        return $"{BuildPilotExecutionContext(marginType, leverage, pilotActivationEnabled: false)} | AiShadowMode=True | HypotheticalSubmit=True";
    }

    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalizedValue = value.Trim();
        return normalizedValue.Length <= maxLength
            ? normalizedValue
            : normalizedValue[..maxLength];
    }

    private static string BuildPilotExecutionContext(string marginType, decimal leverage, bool pilotActivationEnabled)
    {
        return FormattableString.Invariant(
            $"DevelopmentFuturesTestnetPilot=True | PilotActivationEnabled={pilotActivationEnabled} | PilotMarginType={marginType} | PilotLeverage={leverage:0.########}");
    }

    private sealed record ShadowHypotheticalEvaluation(
        bool SubmitAllowed,
        string? BlockReason,
        string? BlockSummary,
        bool PilotSafetyBlocked,
        string? PilotSafetyReason,
        string? PilotSafetySummary,
        bool RiskVetoPresent,
        string? RiskVetoReason,
        string? RiskVetoSummary,
        Guid? DecisionTraceId)
    {
        public static readonly ShadowHypotheticalEvaluation NotEvaluated = new(
            SubmitAllowed: false,
            BlockReason: null,
            BlockSummary: null,
            PilotSafetyBlocked: false,
            PilotSafetyReason: null,
            PilotSafetySummary: null,
            RiskVetoPresent: false,
            RiskVetoReason: null,
            RiskVetoSummary: null,
            DecisionTraceId: null);
    }

    private static string NormalizeRequired(string? value, string parameterName)
    {
        var normalizedValue = value?.Trim();

        if (string.IsNullOrWhiteSpace(normalizedValue))
        {
            throw new ArgumentException("The value is required.", parameterName);
        }

        return normalizedValue;
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

    private static DateTime AlignToIntervalBoundary(DateTime utcNow, string timeframe)
    {
        var normalizedUtcNow = utcNow.Kind == DateTimeKind.Utc
            ? utcNow
            : utcNow.ToUniversalTime();
        var interval = ResolveIntervalDuration(timeframe);
        var ticks = normalizedUtcNow.Ticks - (normalizedUtcNow.Ticks % interval.Ticks);

        return new DateTime(ticks, DateTimeKind.Utc);
    }

    private static TimeSpan ResolveIntervalDuration(string timeframe)
    {
        var normalizedTimeframe = NormalizeRequired(timeframe, nameof(timeframe));
        var magnitudeText = normalizedTimeframe[..^1];
        var unit = normalizedTimeframe[^1];

        if (!int.TryParse(magnitudeText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var magnitude) ||
            magnitude <= 0)
        {
            throw new InvalidOperationException($"Unsupported timeframe '{timeframe}'.");
        }

        return unit switch
        {
            'm' => TimeSpan.FromMinutes(magnitude),
            'h' => TimeSpan.FromHours(magnitude),
            'd' => TimeSpan.FromDays(magnitude),
            _ => throw new InvalidOperationException($"Unsupported timeframe '{timeframe}'.")
        };
    }

    private static decimal ResolvePilotExitQuantity(
        SymbolMetadataSnapshot symbolMetadata,
        decimal openQuantity)
    {
        if (openQuantity <= 0m)
        {
            throw new ExecutionValidationException("ReduceOnlyWithoutOpenPosition", "Reduce-only quantity requires an open position.");
        }

        var candidateQuantity = openQuantity;

        if (symbolMetadata.StepSize > 0m)
        {
            candidateQuantity = AlignDown(candidateQuantity, symbolMetadata.StepSize);
        }

        if (symbolMetadata.QuantityPrecision is int quantityPrecision)
        {
            candidateQuantity = decimal.Round(candidateQuantity, quantityPrecision, MidpointRounding.ToZero);
        }

        if (candidateQuantity <= 0m)
        {
            throw new ExecutionValidationException(
                "ReduceOnlyQuantityInvalid",
                $"Execution blocked because reduce-only quantity could not be resolved for '{symbolMetadata.Symbol}'.");
        }

        return candidateQuantity;
    }

    private static decimal AlignUp(decimal value, decimal increment)
    {
        if (increment <= 0m)
        {
            throw new ExecutionValidationException("Step size must be positive.");
        }

        var remainder = value % increment;

        return remainder == 0m
            ? value
            : value + (increment - remainder);
    }

    private static decimal AlignDown(decimal value, decimal increment)
    {
        if (increment <= 0m)
        {
            throw new ExecutionValidationException("Step size must be positive.");
        }

        return decimal.Floor(value / increment) * increment;
    }

    private sealed record PilotDispatchPlan(
        ExecutionOrderSide Side,
        decimal Quantity,
        bool ReduceOnly);

    private sealed record MarketStateResolution(
        StrategyIndicatorSnapshot? IndicatorSnapshot,
        decimal? ReferencePrice,
        IReadOnlyCollection<MarketCandleSnapshot> HistoricalCandles);

    private bool IsAllowedSymbol(string symbol)
    {
        var allowedSymbols = optionsValue.AllowedSymbols
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => MarketDataSymbolNormalizer.Normalize(item))
            .ToHashSet(StringComparer.Ordinal);

        if (allowedSymbols.Count == 0)
        {
            allowedSymbols.Add(MarketDataSymbolNormalizer.Normalize(optionsValue.DefaultSymbol));
        }

        return allowedSymbols.Contains(symbol);
    }
}

