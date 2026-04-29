using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    ILogger<BotWorkerJobProcessor> logger,
    IOptions<ExecutionRuntimeOptions>? executionRuntimeOptions = null) : IBotWorkerJobProcessor
{
    private const int AiShadowOutcomeCoverageBatchSize = 25;
    private const string ExitSkippedDecisionOutcome = "Skipped";
    private const string ExitSkippedDecisionReasonType = "ExecutionSkip";
    private const string NoOpenPositionForExitDecisionCode = "NoOpenPositionForExit";
    private const string NoClosableQuantityForExitDecisionCode = "NoClosableQuantityForExit";
    private const string OrderExecutionBreakerActiveDecisionCode = "OrderExecutionBreakerActive";
    private const string BotCooldownActiveDecisionCode = "BotCooldownActive";
    private const string SymbolCooldownActiveDecisionCode = "SymbolCooldownActive";
    private const string EntryNotionalSafetyBlockedDecisionCode = "EntryNotionalSafetyBlocked";
    private const string EntryQuantitySizingFailedDecisionCode = "EntryQuantitySizingFailedClosed";
    private const string TakeProfitTriggeredDecisionCode = "TakeProfitTriggered";
    private const string StopLossTriggeredDecisionCode = "StopLossTriggered";
    private const string TrailingStopTriggeredDecisionCode = "TrailingStopTriggered";
    private const string BreakEvenTriggeredDecisionCode = "BreakEvenTriggered";
    private const string EntrySupersededByRuntimeExitQualityDecisionCode = "EntrySupersededByRuntimeExitQuality";
    private const string ReverseBlockedOpenPositionExistsDecisionCode = "ReverseBlockedOpenPositionExists";
    private const string EntryDirectionModeBlockedDecisionCode = "EntryDirectionModeBlocked";
    private const string PositionAdoptionAmbiguousDecisionCode = "PositionAdoptionAmbiguous";
    private const string AutoPositionManagementDisabledDecisionCode = "AutoPositionManagementDisabled";
    private const string LeveragePolicyExceededDecisionCode = "LeveragePolicyExceeded";
    private const string LeveragePolicyAllowedReasonCode = "LeveragePolicyAllowed";
    private const string LeverageAlignmentSkippedForReduceOnlyReasonCode = "LeverageAlignmentSkippedForReduceOnly";
    private const string SymbolExecutionNotAllowedDecisionCode = "SymbolExecutionNotAllowed";
    private const string SymbolAllowlistEmptyDecisionCode = "SymbolAllowlistEmpty";
    private const string SymbolAllowlistAllowedReasonCode = "SymbolAllowlistAllowed";
    private const string SymbolAllowlistSkippedForCloseOnlyReasonCode = "SymbolAllowlistSkippedForCloseOnly";
    private const string ExitCloseOnlyIntentCode = "ExitCloseOnly";
    private const string ExitCloseOnlyBlockedPrivatePlaneStaleDecisionCode = "ExitCloseOnlyBlockedPrivatePlaneStale";
    private const string ExitCloseOnlyBlockedRiskDecisionCode = "ExitCloseOnlyBlockedRisk";
    private const string ExitCloseOnlyBlockedNoOpenPositionDecisionCode = "ExitCloseOnlyBlockedNoOpenPosition";
    private const string ExitCloseOnlyBlockedQuantityInvalidDecisionCode = "ExitCloseOnlyBlockedQuantityInvalid";
    private const string ExitCloseOnlyBlockedUnprofitableLongDecisionCode = "ExitCloseOnlyBlockedUnprofitableLong";
    private const string ExitCloseOnlyBlockedUnprofitableShortDecisionCode = "ExitCloseOnlyBlockedUnprofitableShort";
    private const string ExitCloseOnlyAllowedTakeProfitDecisionCode = "ExitCloseOnlyAllowedTakeProfit";
    private const string ExitReasonReverseSignal = "ReverseSignal";
    private const string ExitReasonTakeProfit = "TakeProfit";
    private const string ExitReasonTrailingTakeProfit = "TrailingTakeProfit";
    private const string ExitReasonStopLoss = "StopLoss";
    private const string ExitReasonRiskExit = "RiskExit";
    private const string ExitReasonManual = "Manual";
    private const string ExitReasonEmergency = "Emergency";
    private const string ExitReasonBlockedUnprofitable = "BlockedUnprofitable";
    private const string ExitReasonPrivatePlaneStale = "PrivatePlaneStale";
    private const string ExitReasonStaleMarketData = "StaleMarketData";
    private const decimal DefaultExitCloseOnlyMinimumProfitPct = 0m;
    private const string ProfitPolicyAppliedToken = "ProfitPolicy=Applied";
    private const string ReverseExitBlockedUnprofitablePolicyReason = "ReverseExitBlockedUnprofitable";
    private const string ReverseSignalProfitablePolicyReason = "ReverseSignalProfitable";
    private const string ReverseSignalProfitPolicyDisabledPolicyReason = "ReverseSignalProfitPolicyDisabled";
    private const string TakeProfitThresholdReachedPolicyReason = "TakeProfitThresholdReached";
    private const string TrailingTakeProfitRetracePolicyReason = "TrailingTakeProfitRetrace";
    private const string StopLossThresholdReachedPolicyReason = "StopLossThresholdReached";
    private const string RiskExitThresholdReachedPolicyReason = "RiskExitThresholdReached";
    private const string PositionAdoptionAdoptedState = "Adopted";
    private const string PositionAdoptionAmbiguousState = "Ambiguous";

    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

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
    private readonly ExecutionRuntimeOptions executionRuntimeOptionsValue = executionRuntimeOptions?.Value ?? new ExecutionRuntimeOptions();

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

        var correlationId = Guid.NewGuid().ToString("N");
        var marketState = await ResolveMarketStateAsync(normalizedSymbol, timeframe, cancellationToken);
        var featureSnapshot = await TryCaptureFeatureSnapshotAsync(bot, exchangeAccount, normalizedSymbol, timeframe, marketState, correlationId, cancellationToken);

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

        using var correlationScope = correlationContextAccessor.BeginScope(
            new CorrelationContext(
                correlationId,
                $"bot-job:{bot.Id:N}",
                correlationId,
                null));

        var executionDispatchMode = optionsValue.ExecutionDispatchMode;
        var signalPersistenceEnvironment = executionDispatchMode;
        StrategySignalGenerationResult signalGenerationResult;

        try
        {
            signalGenerationResult = await strategySignalService.GenerateAsync(
                new GenerateStrategySignalsRequest(
                    publishedVersion.Id,
                    new StrategyEvaluationContext(
                        optionsValue.SignalEvaluationMode,
                        marketState.IndicatorSnapshot),
                    featureSnapshot,
                    signalPersistenceEnvironment),
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
            bot.Id,
            normalizedSymbol,
            executionDispatchMode,
            cancellationToken);
        var signal = await ResolveActionableSignalAsync(
            signalGenerationResult,
            publishedVersion.Id,
            normalizedSymbol,
            timeframe,
            marketState.IndicatorSnapshot.CloseTimeUtc,
            signalPersistenceEnvironment,
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
                await TryEnsureAiShadowOutcomeCoverageAsync(bot.OwnerUserId, cancellationToken);

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

        var currentPosition = await ResolveCurrentPositionSnapshotAsync(
                bot,
                exchangeAccount,
                normalizedSymbol,
                currentNetQuantity,
                executionDispatchMode,
                marketState.ReferencePrice,
                cancellationToken);
        var positionAdoption = currentPosition is null
            ? PositionAdoptionResolution.Skipped
            : await ResolvePositionAdoptionAsync(
                bot,
                exchangeAccount,
                normalizedSymbol,
                currentPosition.NetQuantity,
                cancellationToken);
        string? runtimeExitQualityStatusSummary = null;

        if (currentPosition is not null)
        {
            if (positionAdoption.IsAmbiguous)
            {
                var ambiguousSummary = "Position adoption blocked because multiple bots matched the same owner/exchange account/symbol futures scope.";

                if (signal is not null)
                {
                    if (signal.SignalType == StrategySignalType.Exit)
                    {
                        await WriteExitSkippedDecisionTraceAsync(
                            bot.OwnerUserId,
                            publishedVersion,
                            signal,
                            correlationId,
                            strategyDecisionTrace,
                            PositionAdoptionAmbiguousDecisionCode,
                            ambiguousSummary,
                            currentNetQuantity,
                            cancellationToken,
                            positionAdoptionSummary: positionAdoption.Summary,
                            requestedQuantity: Math.Abs(currentNetQuantity),
                            referencePrice: marketState.ReferencePrice);
                    }
                    else
                    {
                        await WriteEntrySkippedDecisionTraceAsync(
                            bot.OwnerUserId,
                            publishedVersion,
                            signal,
                            correlationId,
                            strategyDecisionTrace,
                            ambiguousSummary,
                            currentNetQuantity,
                            cancellationToken,
                            decisionReasonCode: PositionAdoptionAmbiguousDecisionCode,
                            positionAdoptionSummary: positionAdoption.Summary,
                            referencePrice: marketState.ReferencePrice);
                    }
                }

                logger.LogInformation(
                    "Bot execution pilot blocked automation for BotId {BotId} because open position adoption was ambiguous. Symbol={Symbol}.",
                    bot.Id,
                    normalizedSymbol);
                return BackgroundJobProcessResult.Success();
            }

            var activeReduceOnlyExitOrderExists = await HasActiveReduceOnlyExitOrderAsync(
                bot,
                exchangeAccount,
                currentPosition,
                cancellationToken);
            var autoManageAdoptedPosition = IsAutoManageAdoptedPositionEnabled(positionAdoption);

            if (!activeReduceOnlyExitOrderExists &&
                autoManageAdoptedPosition)
            {
                var runtimeExitQualityEvaluation = await EvaluateRuntimeExitQualityAsync(
                    bot,
                    exchangeAccount,
                    publishedVersion,
                    signal,
                    marketState,
                    currentPosition,
                    cancellationToken);
                runtimeExitQualityStatusSummary = runtimeExitQualityEvaluation.TrailingStatusSummary;
                var runtimeExitQualityTrigger = runtimeExitQualityEvaluation.Trigger;

                if (runtimeExitQualityTrigger is not null &&
                    (signal is null || signal.SignalType != StrategySignalType.Exit))
                {
                    runtimeExitQualityTrigger = runtimeExitQualityTrigger with
                    {
                        DecisionSummary = AppendPositionAdoptionSummary(
                            runtimeExitQualityTrigger.DecisionSummary,
                            positionAdoption.Summary)
                    };

                    if (signal is not null && signal.SignalType == StrategySignalType.Entry)
                    {
                        await WriteEntrySkippedDecisionTraceAsync(
                            bot.OwnerUserId,
                            publishedVersion,
                            signal,
                            correlationId,
                            strategyDecisionTrace,
                            $"Entry signal was superseded because runtime exit quality triggered for {signal.Symbol}. {runtimeExitQualityTrigger.DecisionSummary}",
                            currentNetQuantity,
                            cancellationToken,
                            decisionReasonCode: EntrySupersededByRuntimeExitQualityDecisionCode,
                            referencePrice: marketState.ReferencePrice);
                    }

                    signal = await PersistRuntimeExitSignalAsync(
                        bot,
                        publishedVersion,
                        signal,
                        marketState,
                        runtimeExitQualityTrigger,
                        signalPersistenceEnvironment,
                        correlationId,
                        cancellationToken);
                    strategyDecisionTrace = await ResolveLatestDecisionTraceAsync(
                        correlationId,
                        bot.OwnerUserId,
                        normalizedSymbol,
                        timeframe,
                        cancellationToken);
                }
            }
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

        var entryDirection = signal.SignalType == StrategySignalType.Entry
            ? ResolveSignalDirection(signal)
            : StrategyTradeDirection.Neutral;
        CloseOnlyExecutionIntent? closeOnlyIntent = null;
        var entrySymbolAllowlistPolicy = signal.SignalType == StrategySignalType.Entry
            ? EvaluateSymbolExecutionAllowlist(normalizedSymbol, reduceOnly: false)
            : SymbolExecutionAllowlistEvaluation.NotConfigured;
        var activeSymbolAllowlistPolicy = entrySymbolAllowlistPolicy;

        if (signal.SignalType == StrategySignalType.Entry &&
            currentNetQuantity != 0m &&
            IsActionableDirection(entryDirection))
        {
            var currentPositionDirection = currentPosition?.Direction
                ?? (currentNetQuantity > 0m ? StrategyTradeDirection.Long : StrategyTradeDirection.Short);

            if (currentPositionDirection == entryDirection)
            {
                var sameDirectionTrailingSummary = runtimeExitQualityStatusSummary;
                if (string.IsNullOrWhiteSpace(sameDirectionTrailingSummary) &&
                    optionsValue.EnableTrailingTakeProfit &&
                    (!marketState.ReferencePrice.HasValue || marketState.ReferencePrice.Value <= 0m) &&
                    currentPosition is not null)
                {
                    sameDirectionTrailingSummary = BuildTrailingTakeProfitStatusSummary(
                        currentPosition.Direction,
                        "Blocked",
                        "ReferencePriceUnavailable",
                        currentPosition.EntryPrice,
                        null,
                        null,
                        null,
                        null);
                }

                if (entrySymbolAllowlistPolicy.IsBlocked)
                {
                    var blockedAllowlistSummary = PrependDecisionSummarySegment(
                        entrySymbolAllowlistPolicy.Summary,
                        PrependDecisionSummarySegment(
                            sameDirectionTrailingSummary,
                            positionAdoption.Summary));
                    await WriteEntrySkippedDecisionTraceAsync(
                        bot.OwnerUserId,
                        publishedVersion,
                        signal,
                        correlationId,
                        strategyDecisionTrace,
                        blockedAllowlistSummary,
                        currentNetQuantity,
                        cancellationToken,
                        decisionReasonCode: entrySymbolAllowlistPolicy.ReasonCode,
                        referencePrice: marketState.ReferencePrice);

                    logger.LogInformation(
                        "Bot execution pilot blocked entry for BotId {BotId} because symbol execution allowlist failed closed before same-direction suppression. Symbol={Symbol} ReasonCode={ReasonCode} StrategySignalId={StrategySignalId}.",
                        bot.Id,
                        signal.Symbol,
                        entrySymbolAllowlistPolicy.ReasonCode,
                        signal.StrategySignalId);
                    return BackgroundJobProcessResult.PermanentFailure(entrySymbolAllowlistPolicy.ReasonCode);
                }

                var sameDirectionSummary = PrependDecisionSummarySegment(
                    entrySymbolAllowlistPolicy.Summary,
                    PrependDecisionSummarySegment(
                        sameDirectionTrailingSummary,
                        $"{positionAdoption.Summary}; Entry signal was suppressed because an open {entryDirection.ToString().ToLowerInvariant()} position already exists for {signal.Symbol} on the selected exchange account."));
                await WriteEntrySkippedDecisionTraceAsync(
                    bot.OwnerUserId,
                    publishedVersion,
                    signal,
                    correlationId,
                    strategyDecisionTrace,
                    sameDirectionSummary,
                    currentNetQuantity,
                    cancellationToken,
                    decisionReasonCode: ResolveSameDirectionEntrySuppressedDecisionCode(entryDirection),
                    referencePrice: marketState.ReferencePrice);

                logger.LogInformation(
                    "Bot execution pilot suppressed same-direction entry for BotId {BotId} because an open {Direction} position already exists for {Symbol}. StrategySignalId={StrategySignalId}.",
                    bot.Id,
                    entryDirection,
                    signal.Symbol,
                    signal.StrategySignalId);
                return BackgroundJobProcessResult.Success();
            }

            if (!IsAutoManageAdoptedPositionEnabled(positionAdoption))
            {
                await WriteExitSkippedDecisionTraceAsync(
                    bot.OwnerUserId,
                    publishedVersion,
                    signal,
                    correlationId,
                    strategyDecisionTrace,
                    AutoPositionManagementDisabledDecisionCode,
                    PrependDecisionSummarySegment(
                        activeSymbolAllowlistPolicy.Summary,
                        BuildAutoPositionManagementDisabledReverseSummary(
                            signal.Symbol,
                            currentPositionDirection == StrategyTradeDirection.Long
                                ? ExecutionOrderSide.Sell
                                : ExecutionOrderSide.Buy,
                            currentNetQuantity,
                            positionAdoption.Summary)),
                    currentNetQuantity,
                    cancellationToken,
                    positionAdoptionSummary: positionAdoption.Summary,
                    requestedQuantity: Math.Abs(currentNetQuantity),
                    referencePrice: marketState.ReferencePrice);

                logger.LogInformation(
                    "Bot execution pilot blocked reverse close-only automation for BotId {BotId} because adopted position auto-management is disabled. Symbol={Symbol} StrategySignalId={StrategySignalId}.",
                    bot.Id,
                    signal.Symbol,
                    signal.StrategySignalId);
                return BackgroundJobProcessResult.Success();
            }

            closeOnlyIntent = new CloseOnlyExecutionIntent(
                CurrentPositionDirection: currentPositionDirection,
                OpenPositionQuantity: currentNetQuantity,
                CloseSide: currentPositionDirection == StrategyTradeDirection.Long
                    ? ExecutionOrderSide.Sell
                    : ExecutionOrderSide.Buy,
                ExitPnlGuardSummary: null,
                PositionAdoptionSummary: positionAdoption.Summary);
            activeSymbolAllowlistPolicy = EvaluateSymbolExecutionAllowlist(normalizedSymbol, reduceOnly: true);
            signal = signal with { SignalType = StrategySignalType.Exit };
            entryDirection = StrategyTradeDirection.Neutral;

            logger.LogInformation(
                "Bot execution pilot converted an opposing entry into a close-only exit candidate. BotId={BotId} Symbol={Symbol} CurrentPositionDirection={CurrentDirection} CloseSide={CloseSide} StrategySignalId={StrategySignalId}.",
                bot.Id,
                signal.Symbol,
                closeOnlyIntent.CurrentPositionDirection,
                closeOnlyIntent.CloseSide,
                signal.StrategySignalId);

            var closeOnlyPnlGuard = EvaluateExitCloseOnlyPnlGuard(
                signal.Symbol,
                currentPosition,
                symbolMetadata,
                marketState.ReferencePrice,
                positionAdoption.Summary);
            if (closeOnlyPnlGuard.IsBlocked)
            {
                await WriteExitSkippedDecisionTraceAsync(
                    bot.OwnerUserId,
                    publishedVersion,
                    signal,
                    correlationId,
                    strategyDecisionTrace,
                    closeOnlyPnlGuard.DecisionReasonCode,
                    PrependDecisionSummarySegment(
                        activeSymbolAllowlistPolicy.Summary,
                        closeOnlyPnlGuard.DecisionSummary),
                    currentNetQuantity,
                    cancellationToken,
                    requestedQuantity: closeOnlyPnlGuard.CloseQuantity,
                    referencePrice: marketState.ReferencePrice);

                logger.LogInformation(
                    "Bot execution pilot blocked close-only exit for BotId {BotId} because estimated PnL did not satisfy the minimum threshold. Symbol={Symbol} DecisionReasonCode={DecisionReasonCode} StrategySignalId={StrategySignalId}.",
                    bot.Id,
                    signal.Symbol,
                    closeOnlyPnlGuard.DecisionReasonCode,
                    signal.StrategySignalId);
                return BackgroundJobProcessResult.Success();
            }

            closeOnlyIntent = closeOnlyIntent with { ExitPnlGuardSummary = closeOnlyPnlGuard.GuardSummary };
        }

        if (signal.SignalType == StrategySignalType.Entry && entrySymbolAllowlistPolicy.IsBlocked)
        {
            await WriteEntrySkippedDecisionTraceAsync(
                bot.OwnerUserId,
                publishedVersion,
                signal,
                correlationId,
                strategyDecisionTrace,
                entrySymbolAllowlistPolicy.Summary ?? $"SymbolExecutionAllowlist=Applied; SelectedSymbol={signal.Symbol}",
                currentNetQuantity,
                cancellationToken,
                decisionReasonCode: entrySymbolAllowlistPolicy.ReasonCode,
                referencePrice: marketState.ReferencePrice);

            logger.LogInformation(
                "Bot execution pilot blocked entry for BotId {BotId} because symbol execution allowlist failed closed. Symbol={Symbol} ReasonCode={ReasonCode} StrategySignalId={StrategySignalId}.",
                bot.Id,
                signal.Symbol,
                entrySymbolAllowlistPolicy.ReasonCode,
                signal.StrategySignalId);
            return BackgroundJobProcessResult.PermanentFailure(entrySymbolAllowlistPolicy.ReasonCode);
        }

        if (signal.SignalType == StrategySignalType.Entry &&
            TryResolveEntryDirectionModeBlock(
                bot,
                entryDirection,
                out var directionModeBlockSummary))
        {
            await WriteEntrySkippedDecisionTraceAsync(
                bot.OwnerUserId,
                publishedVersion,
                signal,
                correlationId,
                strategyDecisionTrace,
                PrependDecisionSummarySegment(
                    entrySymbolAllowlistPolicy.Summary,
                    directionModeBlockSummary!),
                currentNetQuantity,
                cancellationToken,
                decisionReasonCode: EntryDirectionModeBlockedDecisionCode,
                referencePrice: marketState.ReferencePrice);

            logger.LogInformation(
                "Bot execution pilot skipped entry dispatch for BotId {BotId} because bot direction mode {DirectionMode} blocked the {Direction} request. Symbol={Symbol} StrategySignalId={StrategySignalId}.",
                bot.Id,
                bot.DirectionMode,
                entryDirection,
                signal.Symbol,
                signal.StrategySignalId);
            return BackgroundJobProcessResult.Success();
        }

        if (signal.SignalType == StrategySignalType.Entry &&
            TryResolveEntryRegimeBlock(
                marketState,
                entryDirection,
                marketState.ReferencePrice,
                out var regimeBlockSummary,
                out var regimeDriverSummary))
        {
            await WriteEntrySkippedDecisionTraceAsync(
                bot.OwnerUserId,
                publishedVersion,
                signal,
                correlationId,
                strategyDecisionTrace,
                PrependDecisionSummarySegment(
                    entrySymbolAllowlistPolicy.Summary,
                    regimeBlockSummary!),
                currentNetQuantity,
                cancellationToken,
                decisionReasonCode: ResolveEntryRegimeFilterBlockedDecisionCode(entryDirection),
                referencePrice: marketState.ReferencePrice);

            logger.LogInformation(
                "Bot execution pilot skipped entry dispatch for BotId {BotId} because regime-aware entry discipline blocked the request. Symbol={Symbol}. Direction={Direction}. Drivers={Drivers} StrategySignalId={StrategySignalId}.",
                bot.Id,
                signal.Symbol,
                entryDirection,
                regimeDriverSummary,
                signal.StrategySignalId);
            return BackgroundJobProcessResult.Success();
        }

        if (signal.SignalType == StrategySignalType.Entry)
        {
            var hysteresisSummary = await ResolveEntryHysteresisSummaryAsync(
                bot,
                exchangeAccount,
                entryDirection,
                marketState.ReferencePrice,
                cancellationToken);

            if (!string.IsNullOrWhiteSpace(hysteresisSummary))
            {
                await WriteEntrySkippedDecisionTraceAsync(
                    bot.OwnerUserId,
                    publishedVersion,
                    signal,
                    correlationId,
                    strategyDecisionTrace,
                    PrependDecisionSummarySegment(
                        entrySymbolAllowlistPolicy.Summary,
                        hysteresisSummary),
                    currentNetQuantity,
                    cancellationToken,
                    decisionReasonCode: ResolveEntryHysteresisActiveDecisionCode(entryDirection),
                    referencePrice: marketState.ReferencePrice);

                logger.LogInformation(
                    "Bot execution pilot skipped entry dispatch for BotId {BotId} because hysteresis is active. Symbol={Symbol} StrategySignalId={StrategySignalId}.",
                    bot.Id,
                    signal.Symbol,
                    signal.StrategySignalId);
                return BackgroundJobProcessResult.Success();
            }
        }

        if (signal.SignalType == StrategySignalType.Exit &&
            currentNetQuantity != 0m &&
            currentPosition is not null &&
            !IsAutoManageAdoptedPositionEnabled(positionAdoption))
        {
            await WriteExitSkippedDecisionTraceAsync(
                bot.OwnerUserId,
                publishedVersion,
                signal,
                correlationId,
                strategyDecisionTrace,
                AutoPositionManagementDisabledDecisionCode,
                $"Exit signal was skipped because adopted position auto-management is disabled for {signal.Symbol} on the selected exchange account.",
                currentNetQuantity,
                cancellationToken,
                positionAdoptionSummary: positionAdoption.Summary,
                requestedQuantity: Math.Abs(currentNetQuantity),
                referencePrice: marketState.ReferencePrice);

            logger.LogInformation(
                "Bot execution pilot skipped exit dispatch for BotId {BotId} because adopted position auto-management is disabled. Symbol={Symbol} StrategySignalId={StrategySignalId}.",
                bot.Id,
                signal.Symbol,
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
                bot.Id,
                signal,
                symbolMetadata,
                marketState.ReferencePrice.Value,
                executionDispatchMode,
                cancellationToken);
        }
        catch (ExecutionValidationException exception)
        {
            if (signal.SignalType == StrategySignalType.Exit &&
                (string.Equals(exception.ReasonCode, "ReduceOnlyWithoutOpenPosition", StringComparison.Ordinal) ||
                 string.Equals(exception.ReasonCode, "ReduceOnlyQuantityInvalid", StringComparison.Ordinal)))
            {
                var decisionReasonCode = closeOnlyIntent is not null
                    ? string.Equals(exception.ReasonCode, "ReduceOnlyQuantityInvalid", StringComparison.Ordinal)
                        ? ExitCloseOnlyBlockedQuantityInvalidDecisionCode
                        : ExitCloseOnlyBlockedNoOpenPositionDecisionCode
                    : string.Equals(exception.ReasonCode, "ReduceOnlyQuantityInvalid", StringComparison.Ordinal)
                        ? NoClosableQuantityForExitDecisionCode
                        : NoOpenPositionForExitDecisionCode;
                var decisionSummary = string.Equals(exception.ReasonCode, "ReduceOnlyQuantityInvalid", StringComparison.Ordinal)
                    ? closeOnlyIntent is not null
                        ? $"Close-only exit candidate was skipped because no valid reduce-only quantity could be resolved for {signal.Symbol} on the selected exchange account."
                        : $"Exit signal was skipped because no closable reduce-only quantity could be resolved for {signal.Symbol} on the selected exchange account."
                    : closeOnlyIntent is not null
                        ? $"Close-only exit candidate was skipped because no open position exists for {signal.Symbol} on the selected exchange account."
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

            if (signal.SignalType == StrategySignalType.Entry &&
                (string.Equals(exception.ReasonCode, EntryNotionalSafetyBlockedDecisionCode, StringComparison.Ordinal) ||
                 string.Equals(exception.ReasonCode, EntryQuantitySizingFailedDecisionCode, StringComparison.Ordinal)))
            {
                await WriteEntrySkippedDecisionTraceAsync(
                    bot.OwnerUserId,
                    publishedVersion,
                    signal,
                    correlationId,
                    strategyDecisionTrace,
                    exception.Message,
                    currentNetQuantity,
                    cancellationToken,
                    decisionReasonCode: exception.ReasonCode);

                logger.LogInformation(
                    exception,
                    "Bot execution pilot skipped entry dispatch for BotId {BotId} because entry sizing failed closed for {Symbol}. StrategySignalId={StrategySignalId} ReasonCode={ReasonCode}.",
                    bot.Id,
                    normalizedSymbol,
                    signal.StrategySignalId,
                    exception.ReasonCode);
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

        if (TryResolveDispatchSafetyBlock(
                signal,
                symbolMetadata,
                marketState.ReferencePrice.Value,
                dispatchPlan,
                out var dispatchSafetyReasonCode,
                out var dispatchSafetySummary))
        {
            if (signal.SignalType == StrategySignalType.Exit)
            {
                await WriteExitSkippedDecisionTraceAsync(
                    bot.OwnerUserId,
                    publishedVersion,
                    signal,
                    correlationId,
                    strategyDecisionTrace,
                    dispatchSafetyReasonCode!,
                    PrependDecisionSummarySegment(
                        activeSymbolAllowlistPolicy.Summary,
                        dispatchSafetySummary!),
                    currentNetQuantity,
                    cancellationToken,
                    requestedQuantity: dispatchPlan.Quantity,
                    referencePrice: marketState.ReferencePrice.Value);
            }
            else
            {
                await WriteEntrySkippedDecisionTraceAsync(
                    bot.OwnerUserId,
                    publishedVersion,
                    signal,
                    correlationId,
                    strategyDecisionTrace,
                    PrependDecisionSummarySegment(
                        activeSymbolAllowlistPolicy.Summary,
                        dispatchSafetySummary!),
                    currentNetQuantity,
                    cancellationToken,
                    decisionReasonCode: dispatchSafetyReasonCode!,
                    requestedQuantity: dispatchPlan.Quantity,
                    referencePrice: marketState.ReferencePrice.Value);
            }

            logger.LogInformation(
                "Bot execution pilot skipped {SignalType} dispatch for BotId {BotId} because dispatch safety blocked the request. Symbol={Symbol} ReasonCode={ReasonCode} Quantity={Quantity} Price={Price}.",
                signal.SignalType,
                bot.Id,
                normalizedSymbol,
                dispatchSafetyReasonCode,
                dispatchPlan.Quantity,
                marketState.ReferencePrice.Value);
            return BackgroundJobProcessResult.Success();
        }

        var activeOrderExecutionBreaker = await ResolveActiveOrderExecutionBreakerAsync(cancellationToken);
        if (activeOrderExecutionBreaker is not null)
        {
            var breakerSummary = $"Execution signal was skipped because the order execution breaker is {activeOrderExecutionBreaker.StateCode} for {signal.Symbol}. CooldownUntilUtc={activeOrderExecutionBreaker.CooldownUntilUtc?.ToString("O") ?? "none"}; LastErrorCode={activeOrderExecutionBreaker.LastErrorCode ?? "none"}.";

            if (signal.SignalType == StrategySignalType.Exit)
            {
                await WriteExitSkippedDecisionTraceAsync(
                    bot.OwnerUserId,
                    publishedVersion,
                    signal,
                    correlationId,
                    strategyDecisionTrace,
                    OrderExecutionBreakerActiveDecisionCode,
                    PrependDecisionSummarySegment(
                        activeSymbolAllowlistPolicy.Summary,
                        breakerSummary),
                    currentNetQuantity,
                    cancellationToken,
                    requestedQuantity: dispatchPlan.Quantity,
                    referencePrice: marketState.ReferencePrice.Value);
            }
            else
            {
                await WriteEntrySkippedDecisionTraceAsync(
                    bot.OwnerUserId,
                    publishedVersion,
                    signal,
                    correlationId,
                    strategyDecisionTrace,
                    PrependDecisionSummarySegment(
                        activeSymbolAllowlistPolicy.Summary,
                        breakerSummary),
                    currentNetQuantity,
                    cancellationToken,
                    decisionReasonCode: OrderExecutionBreakerActiveDecisionCode,
                    requestedQuantity: dispatchPlan.Quantity,
                    referencePrice: marketState.ReferencePrice.Value);
            }

            logger.LogInformation(
                "Bot execution pilot skipped {SignalType} dispatch for BotId {BotId} because OrderExecution breaker is active. Symbol={Symbol} State={StateCode} CooldownUntilUtc={CooldownUntilUtc}.",
                signal.SignalType,
                bot.Id,
                normalizedSymbol,
                activeOrderExecutionBreaker.StateCode,
                activeOrderExecutionBreaker.CooldownUntilUtc);
            return BackgroundJobProcessResult.Success();
        }

        var symbolAllowlistPolicy = signal.SignalType == StrategySignalType.Entry
            ? entrySymbolAllowlistPolicy
            : activeSymbolAllowlistPolicy;
        if (symbolAllowlistPolicy.IsBlocked)
        {
            var allowlistExecutionContext = AppendExecutionIntentContext(
                AppendPilotContextSegment(
                    BuildPilotExecutionContext(
                        marginType!,
                        leverage!.Value,
                        pilotActivationEnabled,
                        executionDispatchMode),
                    symbolAllowlistPolicy.Summary),
                signal.SignalType,
                closeOnlyIntent,
                dispatchPlan);

            if (signal.SignalType == StrategySignalType.Exit)
            {
                await WriteExitSkippedDecisionTraceAsync(
                    bot.OwnerUserId,
                    publishedVersion,
                    signal,
                    correlationId,
                    strategyDecisionTrace,
                    symbolAllowlistPolicy.ReasonCode,
                    allowlistExecutionContext,
                    currentNetQuantity,
                    cancellationToken,
                    requestedQuantity: dispatchPlan.Quantity,
                    referencePrice: marketState.ReferencePrice.Value);
            }
            else
            {
                await WriteEntrySkippedDecisionTraceAsync(
                    bot.OwnerUserId,
                    publishedVersion,
                    signal,
                    correlationId,
                    strategyDecisionTrace,
                    allowlistExecutionContext,
                    currentNetQuantity,
                    cancellationToken,
                    decisionReasonCode: symbolAllowlistPolicy.ReasonCode,
                    requestedQuantity: dispatchPlan.Quantity,
                    referencePrice: marketState.ReferencePrice.Value);
            }

            logger.LogInformation(
                "Bot execution pilot blocked {SignalType} dispatch for BotId {BotId} because symbol execution allowlist failed closed. Symbol={Symbol} ReasonCode={ReasonCode}.",
                signal.SignalType,
                bot.Id,
                normalizedSymbol,
                symbolAllowlistPolicy.ReasonCode);
            return BackgroundJobProcessResult.PermanentFailure(symbolAllowlistPolicy.ReasonCode);
        }

        var leveragePolicy = EvaluatePilotLeveragePolicy(bot, dispatchPlan);
        if (leveragePolicy.IsBlocked)
        {
            var leveragePolicySummary = AppendExecutionIntentContext(
                AppendPilotContextSegment(
                    BuildPilotExecutionContext(
                        marginType!,
                        leveragePolicy.EffectiveLeverage,
                        pilotActivationEnabled,
                        executionDispatchMode,
                        leveragePolicy.Summary),
                    symbolAllowlistPolicy.Summary),
                signal.SignalType,
                closeOnlyIntent,
                dispatchPlan);

            if (signal.SignalType == StrategySignalType.Exit)
            {
                await WriteExitSkippedDecisionTraceAsync(
                    bot.OwnerUserId,
                    publishedVersion,
                    signal,
                    correlationId,
                    strategyDecisionTrace,
                    leveragePolicy.ReasonCode,
                    leveragePolicySummary,
                    currentNetQuantity,
                    cancellationToken,
                    requestedQuantity: dispatchPlan.Quantity,
                    referencePrice: marketState.ReferencePrice.Value);
            }
            else
            {
                await WriteEntrySkippedDecisionTraceAsync(
                    bot.OwnerUserId,
                    publishedVersion,
                    signal,
                    correlationId,
                    strategyDecisionTrace,
                    leveragePolicySummary,
                    currentNetQuantity,
                    cancellationToken,
                    decisionReasonCode: leveragePolicy.ReasonCode,
                    requestedQuantity: dispatchPlan.Quantity,
                    referencePrice: marketState.ReferencePrice.Value);
            }

            logger.LogInformation(
                "Bot execution pilot blocked {SignalType} dispatch for BotId {BotId} because leverage policy failed closed. Symbol={Symbol} ReasonCode={ReasonCode} EffectiveLeverage={EffectiveLeverage} MaxAllowedLeverage={MaxAllowedLeverage}.",
                signal.SignalType,
                bot.Id,
                signal.Symbol,
                leveragePolicy.ReasonCode,
                leveragePolicy.EffectiveLeverage,
                leveragePolicy.MaxAllowedLeverage);
            return BackgroundJobProcessResult.PermanentFailure(leveragePolicy.ReasonCode);
        }

        var pilotExecutionContext = AppendExecutionIntentContext(
            AppendPilotContextSegment(
                BuildPilotExecutionContext(
                    marginType!,
                    leveragePolicy.EffectiveLeverage,
                    pilotActivationEnabled,
                    executionDispatchMode,
                    leveragePolicy.Summary),
                symbolAllowlistPolicy.Summary),
            signal.SignalType,
            closeOnlyIntent,
            dispatchPlan);
        var preSubmitPilotEvaluation = await userExecutionOverrideGuard.EvaluateAsync(
            new UserExecutionOverrideEvaluationRequest(
                bot.OwnerUserId,
                signal.Symbol,
                executionDispatchMode,
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
                Plane: ExchangeDataPlane.Futures,
                ExchangeAccountId: exchangeAccount.Id,
                ReduceOnly: dispatchPlan.ReduceOnly),
            cancellationToken);
        var concurrencyGuardSummary = ExtractConcurrencyGuardSummary(preSubmitPilotEvaluation.GuardSummary);

        if (TryResolveCooldownSkip(preSubmitPilotEvaluation, out var cooldownReasonCode, out var cooldownSummary))
        {
            if (signal.SignalType == StrategySignalType.Exit)
            {
                await WriteExitSkippedDecisionTraceAsync(
                    bot.OwnerUserId,
                    publishedVersion,
                    signal,
                    correlationId,
                    strategyDecisionTrace,
                    cooldownReasonCode!,
                    cooldownSummary!,
                    currentNetQuantity,
                    cancellationToken,
                    requestedQuantity: dispatchPlan.Quantity,
                    referencePrice: marketState.ReferencePrice.Value);
            }
            else
            {
                await WriteEntrySkippedDecisionTraceAsync(
                    bot.OwnerUserId,
                    publishedVersion,
                    signal,
                    correlationId,
                    strategyDecisionTrace,
                    cooldownSummary!,
                    currentNetQuantity,
                    cancellationToken,
                    decisionReasonCode: cooldownReasonCode!,
                    requestedQuantity: dispatchPlan.Quantity,
                    referencePrice: marketState.ReferencePrice.Value);
            }

            logger.LogInformation(
                "Bot execution pilot skipped {SignalType} dispatch for BotId {BotId} because pre-submit cooldown guard blocked the request. Symbol={Symbol} BlockCode={BlockCode}.",
                signal.SignalType,
                bot.Id,
                signal.Symbol,
                cooldownReasonCode);
            return BackgroundJobProcessResult.Success();
        }

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

        if (preSubmitPilotEvaluation.IsBlocked)
        {
            var blockCode = closeOnlyIntent is not null && (preSubmitPilotEvaluation.RiskEvaluation?.IsVetoed ?? false)
                ? ExitCloseOnlyBlockedRiskDecisionCode
                : preSubmitPilotEvaluation.BlockCode ?? "UserExecutionOverrideBlocked";
            var blockSummarySource = string.IsNullOrWhiteSpace(concurrencyGuardSummary)
                ? preSubmitPilotEvaluation.Message ?? "Execution blocked by user execution override guard."
                : PrependDecisionSummarySegment(
                    concurrencyGuardSummary,
                    preSubmitPilotEvaluation.Message ?? "Execution blocked by user execution override guard.");
            var blockSummary = AppendExitReasonToken(
                blockSummarySource,
                blockCode,
                isExitCloseOnly: signal.SignalType == StrategySignalType.Exit);

            if (signal.SignalType == StrategySignalType.Exit)
            {
                await WriteExitSkippedDecisionTraceAsync(
                    bot.OwnerUserId,
                    publishedVersion,
                    signal,
                    correlationId,
                    strategyDecisionTrace,
                    blockCode,
                    blockSummary,
                    currentNetQuantity,
                    cancellationToken,
                    requestedQuantity: dispatchPlan.Quantity,
                    referencePrice: marketState.ReferencePrice.Value);
            }
            else
            {
                await WriteEntrySkippedDecisionTraceAsync(
                    bot.OwnerUserId,
                    publishedVersion,
                    signal,
                    correlationId,
                    strategyDecisionTrace,
                    blockSummary,
                    currentNetQuantity,
                    cancellationToken,
                    decisionReasonCode: blockCode,
                    requestedQuantity: dispatchPlan.Quantity,
                    referencePrice: marketState.ReferencePrice.Value);
            }

            logger.LogInformation(
                "Bot execution pilot skipped {SignalType} dispatch for BotId {BotId} because pre-submit override guard blocked the request. Symbol={Symbol} BlockCode={BlockCode}.",
                signal.SignalType,
                bot.Id,
                signal.Symbol,
                blockCode);
            return BackgroundJobProcessResult.Success();
        }

        if (!string.IsNullOrWhiteSpace(concurrencyGuardSummary))
        {
            pilotExecutionContext = AppendPilotContextSegment(pilotExecutionContext, concurrencyGuardSummary);
        }

        try
        {
            var dispatchIdempotencyKey = BuildDispatchIdempotencyKey(
                signal,
                executionDispatchMode,
                dispatchPlan);
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
                    IsDemo: executionDispatchMode == ExecutionEnvironment.Demo ? true : null,
                    IdempotencyKey: dispatchIdempotencyKey,
                    CorrelationId: null,
                    ParentCorrelationId: null,
                    Context: pilotExecutionContext,
                    ReduceOnly: dispatchPlan.ReduceOnly,
                    RequestedEnvironment: executionDispatchMode),
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
        string correlationId,
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
                    marketState.HistoricalCandles,
                    correlationId),
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
        ExecutionEnvironment executionEnvironment,
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
                    entity.ExecutionEnvironment == executionEnvironment &&
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

    private async Task<CurrentPositionSnapshot?> ResolveCurrentPositionSnapshotAsync(
        TradingBot bot,
        ExchangeAccount exchangeAccount,
        string normalizedSymbol,
        decimal currentNetQuantity,
        ExecutionEnvironment executionDispatchMode,
        decimal? referencePrice,
        CancellationToken cancellationToken)
    {
        if (currentNetQuantity == 0m)
        {
            return null;
        }

        if (UsesInternalDemoExecution(executionDispatchMode))
        {
            return await ResolveCurrentDemoPositionSnapshotAsync(
                bot,
                normalizedSymbol,
                currentNetQuantity,
                referencePrice,
                cancellationToken);
        }

        var positionDirection = currentNetQuantity > 0m
            ? StrategyTradeDirection.Long
            : StrategyTradeDirection.Short;

        var positions = await dbContext.ExchangePositions
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(entity =>
                entity.OwnerUserId == bot.OwnerUserId &&
                entity.ExchangeAccountId == exchangeAccount.Id &&
                entity.Plane == ExchangeDataPlane.Futures &&
                !entity.IsDeleted)
            .ToListAsync(cancellationToken);

        var matchingPositions = positions
            .Where(entity => NormalizePositionSymbol(entity.Symbol) == normalizedSymbol)
            .Where(entity =>
            {
                var signedQuantity = ResolveSignedPositionQuantity(entity);
                return positionDirection == StrategyTradeDirection.Long
                    ? signedQuantity > 0m
                    : signedQuantity < 0m;
            })
            .ToArray();

        var persistedNetQuantity = matchingPositions.Sum(ResolveSignedPositionQuantity);
        var netQuantity = positionDirection == StrategyTradeDirection.Long
            ? (persistedNetQuantity > 0m ? persistedNetQuantity : currentNetQuantity)
            : (persistedNetQuantity < 0m ? persistedNetQuantity : currentNetQuantity);
        if (netQuantity == 0m)
        {
            return null;
        }

        var weightedEntryPrice = 0m;
        if (matchingPositions.Length > 0)
        {
            var totalQuantity = matchingPositions.Sum(entity => Math.Abs(entity.Quantity));
            if (totalQuantity > 0m)
            {
                weightedEntryPrice = matchingPositions.Sum(entity => Math.Abs(entity.Quantity) * entity.EntryPrice) / totalQuantity;
            }
        }

        var breakEvenPrice = matchingPositions
            .Where(entity => entity.BreakEvenPrice > 0m)
            .Select(entity => (decimal?)entity.BreakEvenPrice)
            .OrderByDescending(value => value)
            .FirstOrDefault() ?? weightedEntryPrice;

        var unrealizedProfit = matchingPositions.Sum(entity => entity.UnrealizedProfit);
        var entrySide = positionDirection == StrategyTradeDirection.Long
            ? ExecutionOrderSide.Buy
            : ExecutionOrderSide.Sell;
        var latestFilledEntryOrder = await dbContext.ExecutionOrders
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(entity =>
                entity.OwnerUserId == bot.OwnerUserId &&
                entity.ExchangeAccountId == exchangeAccount.Id &&
                entity.BotId == bot.Id &&
                entity.Symbol == bot.Symbol &&
                entity.Plane == ExchangeDataPlane.Futures &&
                entity.SubmittedToBroker &&
                !entity.ReduceOnly &&
                entity.Side == entrySide &&
                entity.State == ExecutionOrderState.Filled &&
                !entity.IsDeleted)
            .OrderByDescending(entity => entity.CreatedDate)
            .FirstOrDefaultAsync(cancellationToken);

        decimal resolvedEntryPrice = weightedEntryPrice > 0m
            ? weightedEntryPrice
            : latestFilledEntryOrder?.AverageFillPrice.GetValueOrDefault() > 0m
                ? latestFilledEntryOrder!.AverageFillPrice.GetValueOrDefault()
                : latestFilledEntryOrder?.Price ?? 0m;

        if (resolvedEntryPrice <= 0m)
        {
            return null;
        }

        decimal resolvedBreakEvenPrice = breakEvenPrice > 0m
            ? breakEvenPrice
            : resolvedEntryPrice;

        var resolvedEntryOpenedAtUtc = latestFilledEntryOrder?.CreatedDate
            ?? latestFilledEntryOrder?.SubmittedAtUtc
            ?? timeProvider.GetUtcNow().UtcDateTime;

        decimal resolvedUnrealizedProfit = unrealizedProfit != 0m || !referencePrice.HasValue
            ? unrealizedProfit
            : positionDirection == StrategyTradeDirection.Long
                ? NormalizeDecimal((referencePrice.Value - resolvedEntryPrice) * Math.Abs(netQuantity))
                : NormalizeDecimal((resolvedEntryPrice - referencePrice.Value) * Math.Abs(netQuantity));

        return new CurrentPositionSnapshot(
            positionDirection,
            netQuantity,
            resolvedEntryPrice,
            resolvedBreakEvenPrice,
            resolvedUnrealizedProfit,
            resolvedEntryOpenedAtUtc,
            latestFilledEntryOrder?.Id,
            latestFilledEntryOrder?.CreatedDate);
    }

    private async Task<CurrentPositionSnapshot?> ResolveCurrentDemoPositionSnapshotAsync(
        TradingBot bot,
        string normalizedSymbol,
        decimal currentNetQuantity,
        decimal? referencePrice,
        CancellationToken cancellationToken)
    {
        var positionDirection = currentNetQuantity > 0m
            ? StrategyTradeDirection.Long
            : StrategyTradeDirection.Short;
        var demoPositions = await dbContext.DemoPositions
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(entity =>
                entity.OwnerUserId == bot.OwnerUserId &&
                entity.BotId == bot.Id &&
                entity.Symbol == normalizedSymbol &&
                !entity.IsDeleted)
            .ToListAsync(cancellationToken);

        var matchingPositions = demoPositions
            .Where(entity =>
                positionDirection == StrategyTradeDirection.Long
                    ? entity.Quantity > 0m
                    : entity.Quantity < 0m)
            .ToArray();

        if (matchingPositions.Length == 0)
        {
            return null;
        }

        var totalQuantity = matchingPositions.Sum(entity => Math.Abs(entity.Quantity));
        if (totalQuantity <= 0m)
        {
            return null;
        }

        var weightedEntryPrice = matchingPositions.Sum(entity => Math.Abs(entity.Quantity) * entity.AverageEntryPrice) / totalQuantity;
        var latestFillAtUtc = matchingPositions
            .Select(entity => entity.LastFilledAtUtc)
            .Where(value => value.HasValue)
            .OrderByDescending(value => value)
            .FirstOrDefault();
        var latestFilledEntryOrder = await dbContext.ExecutionOrders
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(entity =>
                entity.OwnerUserId == bot.OwnerUserId &&
                entity.BotId == bot.Id &&
                entity.Symbol == normalizedSymbol &&
                entity.Plane == ExchangeDataPlane.Futures &&
                entity.ExecutionEnvironment == ExecutionEnvironment.Demo &&
                entity.ExecutorKind == ExecutionOrderExecutorKind.Virtual &&
                !entity.ReduceOnly &&
                entity.Side == (positionDirection == StrategyTradeDirection.Long ? ExecutionOrderSide.Buy : ExecutionOrderSide.Sell) &&
                entity.State == ExecutionOrderState.Filled &&
                !entity.IsDeleted)
            .OrderByDescending(entity => entity.CreatedDate)
            .FirstOrDefaultAsync(cancellationToken);

        var resolvedEntryPrice = weightedEntryPrice > 0m
            ? weightedEntryPrice
            : latestFilledEntryOrder?.AverageFillPrice.GetValueOrDefault() > 0m
                ? latestFilledEntryOrder!.AverageFillPrice.GetValueOrDefault()
                : latestFilledEntryOrder?.Price ?? 0m;

        if (resolvedEntryPrice <= 0m)
        {
            return null;
        }

        var unrealizedProfit = matchingPositions.Sum(entity => entity.UnrealizedPnl);
        var resolvedUnrealizedProfit = unrealizedProfit != 0m || !referencePrice.HasValue
            ? unrealizedProfit
            : positionDirection == StrategyTradeDirection.Long
                ? NormalizeDecimal((referencePrice.Value - resolvedEntryPrice) * Math.Abs(currentNetQuantity))
                : NormalizeDecimal((resolvedEntryPrice - referencePrice.Value) * Math.Abs(currentNetQuantity));

        return new CurrentPositionSnapshot(
            positionDirection,
            currentNetQuantity,
            resolvedEntryPrice,
            resolvedEntryPrice,
            resolvedUnrealizedProfit,
            latestFillAtUtc ?? latestFilledEntryOrder?.CreatedDate ?? timeProvider.GetUtcNow().UtcDateTime,
            latestFilledEntryOrder?.Id,
            latestFilledEntryOrder?.CreatedDate);
    }

    private async Task<bool> HasActiveReduceOnlyExitOrderAsync(
        TradingBot bot,
        ExchangeAccount exchangeAccount,
        CurrentPositionSnapshot currentPosition,
        CancellationToken cancellationToken)
    {
        var reduceOnlySide = currentPosition.Direction == StrategyTradeDirection.Long
            ? ExecutionOrderSide.Sell
            : ExecutionOrderSide.Buy;

        return await dbContext.ExecutionOrders
            .AsNoTracking()
            .IgnoreQueryFilters()
            .AnyAsync(entity =>
                entity.OwnerUserId == bot.OwnerUserId &&
                entity.ExchangeAccountId == exchangeAccount.Id &&
                entity.BotId == bot.Id &&
                entity.Symbol == bot.Symbol &&
                entity.Plane == ExchangeDataPlane.Futures &&
                entity.SubmittedToBroker &&
                entity.ReduceOnly &&
                entity.Side == reduceOnlySide &&
                !entity.IsDeleted &&
                (entity.State == ExecutionOrderState.Received ||
                 entity.State == ExecutionOrderState.GatePassed ||
                 entity.State == ExecutionOrderState.Dispatching ||
                 entity.State == ExecutionOrderState.Submitted ||
                 entity.State == ExecutionOrderState.PartiallyFilled ||
                 entity.State == ExecutionOrderState.CancelRequested),
                cancellationToken);
    }

    private async Task<RuntimeExitQualityEvaluation> EvaluateRuntimeExitQualityAsync(
        TradingBot bot,
        ExchangeAccount exchangeAccount,
        TradingStrategyVersion publishedVersion,
        StrategySignalSnapshot? sourceSignal,
        MarketStateResolution marketState,
        CurrentPositionSnapshot currentPosition,
        CancellationToken cancellationToken)
    {
        if (!optionsValue.EnableRuntimeExitQuality)
        {
            return new RuntimeExitQualityEvaluation(null, null);
        }

        if (!marketState.ReferencePrice.HasValue ||
            marketState.ReferencePrice.Value <= 0m)
        {
            return new RuntimeExitQualityEvaluation(
                null,
                optionsValue.EnableTrailingTakeProfit
                    ? BuildTrailingTakeProfitStatusSummary(
                        currentPosition.Direction,
                        "Blocked",
                        "ReferencePriceUnavailable",
                        entryPrice: currentPosition.EntryPrice,
                        currentPrice: null,
                        trailingActivationPrice: null,
                        trailingReferencePrice: null,
                        trailingStopPrice: null)
                    : null);
        }

        if (currentPosition.EntryPrice <= 0m ||
            currentPosition.NetQuantity == 0m)
        {
            return new RuntimeExitQualityEvaluation(
                null,
                optionsValue.EnableTrailingTakeProfit
                    ? BuildTrailingTakeProfitStatusSummary(
                        currentPosition.Direction,
                        "Blocked",
                        currentPosition.EntryPrice <= 0m ? "EntryPriceUnavailable" : "PositionQuantityUnavailable",
                        entryPrice: currentPosition.EntryPrice,
                        currentPrice: marketState.ReferencePrice.Value,
                        trailingActivationPrice: null,
                        trailingReferencePrice: null,
                        trailingStopPrice: null)
                    : null);
        }

        var referencePrice = marketState.ReferencePrice.Value;
        var entryPrice = currentPosition.EntryPrice;
        var breakEvenPrice = currentPosition.BreakEvenPrice > 0m
            ? currentPosition.BreakEvenPrice
            : entryPrice;
        string? trailingStatusSummary = null;

        if (currentPosition.Direction == StrategyTradeDirection.Long)
        {
            if (!optionsValue.EnableTrailingTakeProfit &&
                TryResolveUpperPriceBoundary(entryPrice, optionsValue.TakeProfitPercentage, out var takeProfitPrice) &&
                referencePrice >= takeProfitPrice)
            {
                return new RuntimeExitQualityEvaluation(
                    new RuntimeExitQualityTrigger(
                        currentPosition.Direction,
                        TakeProfitTriggeredDecisionCode,
                        BuildRuntimeExitQualitySummary(
                            bot.Symbol,
                            TakeProfitTriggeredDecisionCode,
                            currentPosition.Direction,
                            entryPrice,
                            referencePrice,
                            takeProfitPrice,
                            currentPosition.NetQuantity,
                            peakPrice: null),
                        takeProfitPrice,
                        referencePrice,
                        null),
                    null);
            }

            if (optionsValue.AllowStopLossExit &&
                TryResolveLowerPriceBoundary(entryPrice, optionsValue.StopLossPercentage, out var stopLossPrice) &&
                referencePrice <= stopLossPrice)
            {
                return new RuntimeExitQualityEvaluation(
                    new RuntimeExitQualityTrigger(
                        currentPosition.Direction,
                        StopLossTriggeredDecisionCode,
                        BuildRuntimeExitQualitySummary(
                            bot.Symbol,
                            StopLossTriggeredDecisionCode,
                            currentPosition.Direction,
                            entryPrice,
                            referencePrice,
                            stopLossPrice,
                            currentPosition.NetQuantity,
                            peakPrice: null),
                        stopLossPrice,
                        referencePrice,
                        null),
                    null);
            }

            var peakPriceSinceEntry = await ResolvePeakPriceSinceEntryAsync(
                bot.Symbol,
                sourceSignal?.Timeframe ?? optionsValue.Timeframe,
                currentPosition.EntryOpenedAtUtc,
                marketState,
                cancellationToken);

            if (optionsValue.EnableTrailingTakeProfit &&
                TryResolveUpperPriceBoundary(entryPrice, optionsValue.TrailingStopActivationPercentage, out var trailingActivationPrice))
            {
                if (!peakPriceSinceEntry.HasValue || peakPriceSinceEntry.Value <= 0m)
                {
                    trailingStatusSummary = BuildTrailingTakeProfitStatusSummary(
                        currentPosition.Direction,
                        "Blocked",
                        "TrailingReferenceUnavailable",
                        entryPrice,
                        referencePrice,
                        trailingActivationPrice,
                        null,
                        null);
                }
                else if (peakPriceSinceEntry.Value < trailingActivationPrice)
                {
                    trailingStatusSummary = BuildTrailingTakeProfitStatusSummary(
                        currentPosition.Direction,
                        "Inactive",
                        "ActivationThresholdNotReached",
                        entryPrice,
                        referencePrice,
                        trailingActivationPrice,
                        peakPriceSinceEntry.Value,
                        null);
                }
                else if (TryResolveLowerPriceBoundary(peakPriceSinceEntry.Value, optionsValue.TrailingStopPercentage, out var trailingStopPrice))
                {
                    if (referencePrice <= trailingStopPrice)
                    {
                        var trailingSummary = BuildTrailingTakeProfitStatusSummary(
                            currentPosition.Direction,
                            "ExitAllowed",
                            "TrailingStopRetraceTriggered",
                            entryPrice,
                            referencePrice,
                            trailingActivationPrice,
                            peakPriceSinceEntry.Value,
                            trailingStopPrice);
                        return new RuntimeExitQualityEvaluation(
                            new RuntimeExitQualityTrigger(
                                currentPosition.Direction,
                                TrailingStopTriggeredDecisionCode,
                                BuildRuntimeExitQualitySummary(
                                    bot.Symbol,
                                    TrailingStopTriggeredDecisionCode,
                                    currentPosition.Direction,
                                    entryPrice,
                                    referencePrice,
                                    trailingStopPrice,
                                    currentPosition.NetQuantity,
                                    peakPrice: peakPriceSinceEntry.Value,
                                    trailingStatusSummary: trailingSummary),
                                trailingStopPrice,
                                referencePrice,
                                peakPriceSinceEntry.Value),
                            trailingSummary);
                    }

                    trailingStatusSummary = BuildTrailingTakeProfitStatusSummary(
                        currentPosition.Direction,
                        "Armed",
                        "TrailingStopAwaitingRetrace",
                        entryPrice,
                        referencePrice,
                        trailingActivationPrice,
                        peakPriceSinceEntry.Value,
                        trailingStopPrice);
                }
            }

            if (optionsValue.AllowRiskExit &&
                peakPriceSinceEntry.HasValue &&
                TryResolveUpperPriceBoundary(entryPrice, optionsValue.BreakEvenActivationPercentage, out var breakEvenActivationPrice) &&
                peakPriceSinceEntry.Value >= breakEvenActivationPrice &&
                TryResolveUpperPriceBoundary(breakEvenPrice, optionsValue.BreakEvenBufferPercentage, out var breakEvenFloorPrice) &&
                referencePrice <= breakEvenFloorPrice)
            {
                return new RuntimeExitQualityEvaluation(
                    new RuntimeExitQualityTrigger(
                        currentPosition.Direction,
                        BreakEvenTriggeredDecisionCode,
                        BuildRuntimeExitQualitySummary(
                            bot.Symbol,
                            BreakEvenTriggeredDecisionCode,
                            currentPosition.Direction,
                            entryPrice,
                            referencePrice,
                            breakEvenFloorPrice,
                            currentPosition.NetQuantity,
                            peakPrice: peakPriceSinceEntry.Value,
                            breakEvenPrice: breakEvenPrice),
                        breakEvenFloorPrice,
                        referencePrice,
                        peakPriceSinceEntry.Value),
                    trailingStatusSummary);
            }

            return new RuntimeExitQualityEvaluation(null, trailingStatusSummary);
        }

        if (!optionsValue.EnableTrailingTakeProfit &&
            TryResolveLowerPriceBoundary(entryPrice, optionsValue.TakeProfitPercentage, out var shortTakeProfitPrice) &&
            referencePrice <= shortTakeProfitPrice)
        {
            return new RuntimeExitQualityEvaluation(
                new RuntimeExitQualityTrigger(
                    currentPosition.Direction,
                    TakeProfitTriggeredDecisionCode,
                    BuildRuntimeExitQualitySummary(
                        bot.Symbol,
                        TakeProfitTriggeredDecisionCode,
                        currentPosition.Direction,
                        entryPrice,
                        referencePrice,
                        shortTakeProfitPrice,
                        currentPosition.NetQuantity,
                        peakPrice: null),
                    shortTakeProfitPrice,
                    referencePrice,
                    null),
                null);
        }

        if (optionsValue.AllowStopLossExit &&
            TryResolveUpperPriceBoundary(entryPrice, optionsValue.StopLossPercentage, out var shortStopLossPrice) &&
            referencePrice >= shortStopLossPrice)
        {
            return new RuntimeExitQualityEvaluation(
                new RuntimeExitQualityTrigger(
                    currentPosition.Direction,
                    StopLossTriggeredDecisionCode,
                    BuildRuntimeExitQualitySummary(
                        bot.Symbol,
                        StopLossTriggeredDecisionCode,
                        currentPosition.Direction,
                        entryPrice,
                        referencePrice,
                        shortStopLossPrice,
                        currentPosition.NetQuantity,
                        peakPrice: null),
                    shortStopLossPrice,
                    referencePrice,
                    null),
                null);
        }

        var troughPriceSinceEntry = await ResolveTroughPriceSinceEntryAsync(
            bot.Symbol,
            sourceSignal?.Timeframe ?? optionsValue.Timeframe,
            currentPosition.EntryOpenedAtUtc,
            marketState,
            cancellationToken);

        if (optionsValue.EnableTrailingTakeProfit &&
            TryResolveLowerPriceBoundary(entryPrice, optionsValue.TrailingStopActivationPercentage, out var shortTrailingActivationPrice))
        {
            if (!troughPriceSinceEntry.HasValue || troughPriceSinceEntry.Value <= 0m)
            {
                trailingStatusSummary = BuildTrailingTakeProfitStatusSummary(
                    currentPosition.Direction,
                    "Blocked",
                    "TrailingReferenceUnavailable",
                    entryPrice,
                    referencePrice,
                    shortTrailingActivationPrice,
                    null,
                    null);
            }
            else if (troughPriceSinceEntry.Value > shortTrailingActivationPrice)
            {
                trailingStatusSummary = BuildTrailingTakeProfitStatusSummary(
                    currentPosition.Direction,
                    "Inactive",
                    "ActivationThresholdNotReached",
                    entryPrice,
                    referencePrice,
                    shortTrailingActivationPrice,
                    troughPriceSinceEntry.Value,
                    null);
            }
            else if (TryResolveUpperPriceBoundary(troughPriceSinceEntry.Value, optionsValue.TrailingStopPercentage, out var shortTrailingStopPrice))
            {
                if (referencePrice >= shortTrailingStopPrice)
                {
                    var trailingSummary = BuildTrailingTakeProfitStatusSummary(
                        currentPosition.Direction,
                        "ExitAllowed",
                        "TrailingStopRetraceTriggered",
                        entryPrice,
                        referencePrice,
                        shortTrailingActivationPrice,
                        troughPriceSinceEntry.Value,
                        shortTrailingStopPrice);
                    return new RuntimeExitQualityEvaluation(
                        new RuntimeExitQualityTrigger(
                            currentPosition.Direction,
                            TrailingStopTriggeredDecisionCode,
                            BuildRuntimeExitQualitySummary(
                                bot.Symbol,
                                TrailingStopTriggeredDecisionCode,
                                currentPosition.Direction,
                                entryPrice,
                                referencePrice,
                                shortTrailingStopPrice,
                                currentPosition.NetQuantity,
                                peakPrice: troughPriceSinceEntry.Value,
                                trailingStatusSummary: trailingSummary),
                            shortTrailingStopPrice,
                            referencePrice,
                            troughPriceSinceEntry.Value),
                        trailingSummary);
                }

                trailingStatusSummary = BuildTrailingTakeProfitStatusSummary(
                    currentPosition.Direction,
                    "Armed",
                    "TrailingStopAwaitingRetrace",
                    entryPrice,
                    referencePrice,
                    shortTrailingActivationPrice,
                    troughPriceSinceEntry.Value,
                    shortTrailingStopPrice);
            }
        }

        if (optionsValue.AllowRiskExit &&
            troughPriceSinceEntry.HasValue &&
            TryResolveLowerPriceBoundary(entryPrice, optionsValue.BreakEvenActivationPercentage, out var shortBreakEvenActivationPrice) &&
            troughPriceSinceEntry.Value <= shortBreakEvenActivationPrice &&
            TryResolveLowerPriceBoundary(breakEvenPrice, optionsValue.BreakEvenBufferPercentage, out var shortBreakEvenFloorPrice) &&
            referencePrice >= shortBreakEvenFloorPrice)
        {
            return new RuntimeExitQualityEvaluation(
                new RuntimeExitQualityTrigger(
                    currentPosition.Direction,
                    BreakEvenTriggeredDecisionCode,
                    BuildRuntimeExitQualitySummary(
                        bot.Symbol,
                        BreakEvenTriggeredDecisionCode,
                        currentPosition.Direction,
                        entryPrice,
                        referencePrice,
                        shortBreakEvenFloorPrice,
                        currentPosition.NetQuantity,
                        peakPrice: troughPriceSinceEntry.Value,
                        breakEvenPrice: breakEvenPrice),
                    shortBreakEvenFloorPrice,
                    referencePrice,
                    troughPriceSinceEntry.Value),
                trailingStatusSummary);
        }

        return new RuntimeExitQualityEvaluation(null, trailingStatusSummary);
    }

    private async Task<StrategySignalSnapshot> PersistRuntimeExitSignalAsync(
        TradingBot bot,
        TradingStrategyVersion publishedVersion,
        StrategySignalSnapshot? sourceSignal,
        MarketStateResolution marketState,
        RuntimeExitQualityTrigger trigger,
        ExecutionEnvironment executionEnvironment,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var indicatorSnapshot = sourceSignal?.ExplainabilityPayload.IndicatorSnapshot
            ?? marketState.IndicatorSnapshot
            ?? throw new InvalidOperationException("Runtime exit quality requires a ready indicator snapshot.");
        var generatedAtUtc = timeProvider.GetUtcNow().UtcDateTime;

        var existingSignal = await dbContext.TradingStrategySignals
            .IgnoreQueryFilters()
            .Where(entity =>
                entity.TradingStrategyVersionId == publishedVersion.Id &&
                entity.SignalType == StrategySignalType.Exit &&
                entity.ExecutionEnvironment == executionEnvironment &&
                entity.Symbol == indicatorSnapshot.Symbol &&
                entity.Timeframe == indicatorSnapshot.Timeframe &&
                entity.IndicatorCloseTimeUtc == indicatorSnapshot.CloseTimeUtc &&
                !entity.IsDeleted)
            .OrderByDescending(entity => entity.GeneratedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

        if (existingSignal is not null)
        {
            return await strategySignalService.GetAsync(existingSignal.Id, cancellationToken)
                ?? throw new InvalidOperationException("Existing runtime exit signal could not be materialized.");
        }

        var exitRuleResult = new StrategyRuleResultSnapshot(
            true,
            null,
            "runtime.exitQuality.reasonCode",
            StrategyRuleComparisonOperator.Equals,
            trigger.DecisionReasonCode,
            StrategyRuleOperandKind.String,
            trigger.DecisionReasonCode,
            trigger.DecisionReasonCode,
            Array.Empty<StrategyRuleResultSnapshot>(),
            RuleId: trigger.DecisionReasonCode,
            RuleType: "RuntimeExitQuality",
            Timeframe: indicatorSnapshot.Timeframe,
            Reason: trigger.DecisionSummary);

        var evaluationResult = new StrategyEvaluationResult(
            HasEntryRules: sourceSignal?.ExplainabilityPayload.RuleResultSnapshot.HasEntryRules ?? false,
            EntryMatched: false,
            HasExitRules: true,
            ExitMatched: true,
            HasRiskRules: true,
            RiskPassed: true,
            EntryRuleResult: null,
            ExitRuleResult: exitRuleResult,
            RiskRuleResult: sourceSignal?.ExplainabilityPayload.RuleResultSnapshot.RiskRuleResult,
            Direction: trigger.Direction);

        var confidenceSnapshot = new StrategySignalConfidenceSnapshot(
            100,
            StrategySignalConfidenceBand.High,
            1,
            1,
            true,
            true,
            false,
            RiskVetoReasonCode.None,
            false,
            trigger.DecisionSummary);

        var signalEntity = new TradingStrategySignal
        {
            Id = Guid.NewGuid(),
            OwnerUserId = publishedVersion.OwnerUserId,
            TradingStrategyId = publishedVersion.TradingStrategyId,
            TradingStrategyVersionId = publishedVersion.Id,
            StrategyVersionNumber = publishedVersion.VersionNumber,
            StrategySchemaVersion = publishedVersion.SchemaVersion,
            SignalType = StrategySignalType.Exit,
            ExecutionEnvironment = sourceSignal?.Mode ?? executionEnvironment,
            Symbol = indicatorSnapshot.Symbol,
            Timeframe = indicatorSnapshot.Timeframe,
            IndicatorOpenTimeUtc = indicatorSnapshot.OpenTimeUtc,
            IndicatorCloseTimeUtc = indicatorSnapshot.CloseTimeUtc,
            IndicatorReceivedAtUtc = indicatorSnapshot.ReceivedAtUtc,
            GeneratedAtUtc = generatedAtUtc,
            ExplainabilitySchemaVersion = 1,
            IndicatorSnapshotJson = JsonSerializer.Serialize(indicatorSnapshot, SerializerOptions),
            RuleResultSnapshotJson = JsonSerializer.Serialize(evaluationResult, SerializerOptions),
            RiskEvaluationJson = JsonSerializer.Serialize(confidenceSnapshot, SerializerOptions)
        };

        dbContext.TradingStrategySignals.Add(signalEntity);
        await dbContext.SaveChangesAsync(cancellationToken);

        await traceService.WriteDecisionTraceAsync(
            new DecisionTraceWriteRequest(
                publishedVersion.OwnerUserId,
                indicatorSnapshot.Symbol,
                indicatorSnapshot.Timeframe,
                BuildStrategyVersionLabel(publishedVersion),
                StrategySignalType.Exit.ToString(),
                "Persisted",
                JsonSerializer.Serialize(new
                {
                    RuntimeSignal = true,
                    ExecutionEnvironment = (sourceSignal?.Mode ?? executionEnvironment).ToString(),
                    TriggerDirection = trigger.Direction.ToString(),
                    TriggerReasonCode = trigger.DecisionReasonCode,
                    TriggerSummary = trigger.DecisionSummary,
                    TriggerThresholdPrice = trigger.ThresholdPrice,
                    ReferencePrice = trigger.ReferencePrice,
                    PeakPrice = trigger.PeakPrice,
                    SourceSignalId = sourceSignal?.StrategySignalId,
                    SourceSignalType = sourceSignal?.SignalType.ToString()
                }),
                0,
                CorrelationId: correlationId,
                StrategySignalId: signalEntity.Id,
                DecisionReasonType: "RuntimeExitQuality",
                DecisionReasonCode: trigger.DecisionReasonCode,
                DecisionSummary: trigger.DecisionSummary,
                DecisionAtUtc: generatedAtUtc),
            cancellationToken);

        return new StrategySignalSnapshot(
            signalEntity.Id,
            signalEntity.TradingStrategyId,
            signalEntity.TradingStrategyVersionId,
            signalEntity.StrategyVersionNumber,
            signalEntity.StrategySchemaVersion,
            signalEntity.SignalType,
            signalEntity.ExecutionEnvironment,
            signalEntity.Symbol,
            signalEntity.Timeframe,
            signalEntity.IndicatorOpenTimeUtc,
            signalEntity.IndicatorCloseTimeUtc,
            signalEntity.IndicatorReceivedAtUtc,
            signalEntity.GeneratedAtUtc,
            new StrategySignalExplainabilityPayload(
                signalEntity.ExplainabilitySchemaVersion,
                signalEntity.TradingStrategyId,
                signalEntity.TradingStrategyVersionId,
                signalEntity.StrategyVersionNumber,
                signalEntity.StrategySchemaVersion,
                signalEntity.ExecutionEnvironment,
                indicatorSnapshot,
                evaluationResult,
                confidenceSnapshot,
                new StrategySignalLogExplainabilitySnapshot(
                    "Runtime exit quality",
                    trigger.DecisionSummary,
                    new[] { trigger.DecisionReasonCode },
                    new[] { "runtime-exit", "profitability-quality" }),
                new StrategySignalDuplicateSuppressionSnapshot(
                    false,
                    false,
                    $"runtime-exit:{trigger.DecisionReasonCode}:{signalEntity.Symbol}:{signalEntity.Timeframe}:{signalEntity.IndicatorCloseTimeUtc:O}")));
    }

    private bool TryResolveEntryRegimeBlock(
        MarketStateResolution marketState,
        StrategyTradeDirection entryDirection,
        decimal? referencePrice,
        out string? summary,
        out string? driverSummary)
    {
        summary = null;
        driverSummary = null;

        if (!optionsValue.IsRegimeAwareEntryDisciplineEnabled(entryDirection) ||
            marketState.IndicatorSnapshot is null ||
            !IsActionableDirection(entryDirection))
        {
            return false;
        }

        var indicatorSnapshot = marketState.IndicatorSnapshot;
        var drivers = new List<string>();
        var regimeRsiThreshold = optionsValue.ResolveRegimeMaxEntryRsi(entryDirection);
        var regimeMacdThreshold = optionsValue.ResolveRegimeMacdThreshold(entryDirection);
        var regimeMinBollingerWidthPercentage = optionsValue.ResolveRegimeMinBollingerWidthPercentage(entryDirection);
        var regimeMiddleBandDislocationPercentage = optionsValue.ResolveRegimeMinMiddleBandDislocationPercentage(entryDirection);

        if (indicatorSnapshot.Rsi.IsReady && indicatorSnapshot.Rsi.Value.HasValue)
        {
            if (entryDirection == StrategyTradeDirection.Long &&
                regimeRsiThreshold > 0m &&
                indicatorSnapshot.Rsi.Value.Value >= regimeRsiThreshold)
            {
                drivers.Add($"RSI {indicatorSnapshot.Rsi.Value.Value:0.##} >= {regimeRsiThreshold:0.##}");
            }

            if (entryDirection == StrategyTradeDirection.Short &&
                regimeRsiThreshold > 0m &&
                indicatorSnapshot.Rsi.Value.Value <= regimeRsiThreshold)
            {
                drivers.Add($"RSI {indicatorSnapshot.Rsi.Value.Value:0.##} <= {regimeRsiThreshold:0.##}");
            }
        }

        if (indicatorSnapshot.Macd.IsReady && indicatorSnapshot.Macd.Histogram.HasValue)
        {
            if (entryDirection == StrategyTradeDirection.Long &&
                indicatorSnapshot.Macd.Histogram.Value < regimeMacdThreshold)
            {
                drivers.Add($"MACD histogram {indicatorSnapshot.Macd.Histogram.Value:0.####} < {regimeMacdThreshold:0.####}");
            }

            if (entryDirection == StrategyTradeDirection.Short &&
                indicatorSnapshot.Macd.Histogram.Value > regimeMacdThreshold)
            {
                drivers.Add($"MACD histogram {indicatorSnapshot.Macd.Histogram.Value:0.####} > {regimeMacdThreshold:0.####}");
            }
        }

        var bollingerWidthPercentage = ResolveBollingerWidthPercentage(indicatorSnapshot.Bollinger);
        if (bollingerWidthPercentage.HasValue &&
            regimeMinBollingerWidthPercentage > 0m &&
            bollingerWidthPercentage.Value < regimeMinBollingerWidthPercentage)
        {
            drivers.Add($"Bollinger width {bollingerWidthPercentage.Value:0.####}% < {regimeMinBollingerWidthPercentage:0.####}%");
        }

        if (referencePrice.HasValue &&
            indicatorSnapshot.Bollinger.IsReady &&
            indicatorSnapshot.Bollinger.MiddleBand.HasValue &&
            indicatorSnapshot.Bollinger.MiddleBand.Value > 0m &&
            regimeMiddleBandDislocationPercentage > 0m)
        {
            if (entryDirection == StrategyTradeDirection.Long)
            {
                var priceAboveMiddleBandPercentage = NormalizeDecimal(((referencePrice.Value - indicatorSnapshot.Bollinger.MiddleBand.Value) / indicatorSnapshot.Bollinger.MiddleBand.Value) * 100m);
                if (priceAboveMiddleBandPercentage < regimeMiddleBandDislocationPercentage)
                {
                    drivers.Add($"Price vs middle band {priceAboveMiddleBandPercentage:0.####}% < {regimeMiddleBandDislocationPercentage:0.####}%");
                }
            }
            else if (entryDirection == StrategyTradeDirection.Short)
            {
                var priceBelowMiddleBandPercentage = NormalizeDecimal(((indicatorSnapshot.Bollinger.MiddleBand.Value - referencePrice.Value) / indicatorSnapshot.Bollinger.MiddleBand.Value) * 100m);
                if (priceBelowMiddleBandPercentage < regimeMiddleBandDislocationPercentage)
                {
                    drivers.Add($"Price vs middle band {priceBelowMiddleBandPercentage:0.####}% < {regimeMiddleBandDislocationPercentage:0.####}%");
                }
            }
        }

        if (drivers.Count == 0)
        {
            return false;
        }

        driverSummary = string.Join("; ", drivers);
        var thresholdSummary = optionsValue.BuildRegimeThresholdSummary(entryDirection);
        var liveSummary = BuildEntryRegimeLiveSummary(
            indicatorSnapshot,
            entryDirection,
            referencePrice,
            bollingerWidthPercentage,
            regimeMiddleBandDislocationPercentage);
        summary = $"Entry signal was skipped because regime-aware entry discipline blocked the {entryDirection.ToString().ToLowerInvariant()} request. Thresholds: {thresholdSummary}. Live: {liveSummary}. Drivers: {driverSummary}.";
        return true;
    }

    private async Task<string?> ResolveEntryHysteresisSummaryAsync(
        TradingBot bot,
        ExchangeAccount exchangeAccount,
        StrategyTradeDirection entryDirection,
        decimal? referencePrice,
        CancellationToken cancellationToken)
    {
        if (!optionsValue.EnableEntryHysteresis || !IsActionableDirection(entryDirection))
        {
            return null;
        }

        var exitSide = entryDirection == StrategyTradeDirection.Long
            ? ExecutionOrderSide.Sell
            : ExecutionOrderSide.Buy;

        var latestExitOrder = await dbContext.ExecutionOrders
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(entity =>
                entity.OwnerUserId == bot.OwnerUserId &&
                entity.ExchangeAccountId == exchangeAccount.Id &&
                entity.BotId == bot.Id &&
                entity.Symbol == bot.Symbol &&
                entity.Plane == ExchangeDataPlane.Futures &&
                entity.SubmittedToBroker &&
                entity.ReduceOnly &&
                entity.Side == exitSide &&
                entity.State == ExecutionOrderState.Filled &&
                !entity.IsDeleted)
            .OrderByDescending(entity => entity.CreatedDate)
            .FirstOrDefaultAsync(cancellationToken);

        if (latestExitOrder is null)
        {
            return null;
        }

        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        decimal resolvedExitPrice = latestExitOrder.AverageFillPrice.GetValueOrDefault() > 0m
            ? latestExitOrder.AverageFillPrice.GetValueOrDefault()
            : latestExitOrder.Price;
        var entryHysteresisCooldownMinutes = optionsValue.ResolveEntryHysteresisCooldownMinutes(entryDirection);
        var entryHysteresisReentryBufferPercentage = optionsValue.ResolveEntryHysteresisReentryBufferPercentage(entryDirection);

        if (entryHysteresisCooldownMinutes > 0 &&
            latestExitOrder.CreatedDate.AddMinutes(entryHysteresisCooldownMinutes) > nowUtc)
        {
            return $"Entry signal was skipped because entry hysteresis cooldown is still active after the last filled {entryDirection.ToString().ToLowerInvariant()} exit. LastExitAtUtc={latestExitOrder.CreatedDate:O}; CooldownMinutes={entryHysteresisCooldownMinutes}.";
        }

        if (!referencePrice.HasValue || resolvedExitPrice <= 0m)
        {
            return null;
        }

        if (entryDirection == StrategyTradeDirection.Long &&
            TryResolveUpperPriceBoundary(resolvedExitPrice, entryHysteresisReentryBufferPercentage, out var longReentryThresholdPrice) &&
            referencePrice.Value <= longReentryThresholdPrice)
        {
            return $"Entry signal was skipped because hysteresis re-entry buffer is not yet satisfied for a long rearm. LastExitPrice={resolvedExitPrice:0.########}; ReferencePrice={referencePrice.Value:0.########}; RearmThresholdPrice={longReentryThresholdPrice:0.########}.";
        }

        if (entryDirection == StrategyTradeDirection.Short &&
            TryResolveLowerPriceBoundary(resolvedExitPrice, entryHysteresisReentryBufferPercentage, out var shortReentryThresholdPrice) &&
            referencePrice.Value >= shortReentryThresholdPrice)
        {
            return $"Entry signal was skipped because hysteresis re-entry buffer is not yet satisfied for a short rearm. LastExitPrice={resolvedExitPrice:0.########}; ReferencePrice={referencePrice.Value:0.########}; RearmThresholdPrice={shortReentryThresholdPrice:0.########}.";
        }

        return null;
    }

    private async Task<decimal?> ResolvePeakPriceSinceEntryAsync(
        string symbol,
        string timeframe,
        DateTime entryOpenedAtUtc,
        MarketStateResolution marketState,
        CancellationToken cancellationToken)
    {
        var candidateHighs = marketState.HistoricalCandles
            .Where(snapshot => snapshot.IsClosed && snapshot.CloseTimeUtc >= entryOpenedAtUtc)
            .Select(snapshot => (decimal?)snapshot.HighPrice)
            .ToList();

        if (candidateHighs.Count == 0)
        {
            var interval = ResolveIntervalDuration(timeframe);
            var currentOpenTimeUtc = AlignToIntervalBoundary(timeProvider.GetUtcNow().UtcDateTime, timeframe);
            var lastClosedOpenTimeUtc = currentOpenTimeUtc - interval;
            var requestedStartOpenTimeUtc = AlignToIntervalBoundary(entryOpenedAtUtc, timeframe);
            var fallbackStartOpenTimeUtc = lastClosedOpenTimeUtc - ((optionsValue.PrimeHistoricalCandleCount - 1) * interval);
            var startOpenTimeUtc = requestedStartOpenTimeUtc > fallbackStartOpenTimeUtc
                ? requestedStartOpenTimeUtc
                : fallbackStartOpenTimeUtc;

            var historicalCandles = await historicalKlineClient.GetClosedCandlesAsync(
                symbol,
                timeframe,
                startOpenTimeUtc,
                lastClosedOpenTimeUtc,
                optionsValue.PrimeHistoricalCandleCount,
                cancellationToken);

            candidateHighs.AddRange(historicalCandles
                .Where(snapshot => snapshot.IsClosed && snapshot.CloseTimeUtc >= entryOpenedAtUtc)
                .Select(snapshot => (decimal?)snapshot.HighPrice));
        }

        if (marketState.ReferencePrice.HasValue)
        {
            candidateHighs.Add(marketState.ReferencePrice.Value);
        }

        return candidateHighs
            .Where(value => value.HasValue && value.Value > 0m)
            .DefaultIfEmpty(null)
            .Max();
    }


    private async Task<decimal?> ResolveTroughPriceSinceEntryAsync(
        string symbol,
        string timeframe,
        DateTime entryOpenedAtUtc,
        MarketStateResolution marketState,
        CancellationToken cancellationToken)
    {
        var candidateLows = marketState.HistoricalCandles
            .Where(snapshot => snapshot.IsClosed && snapshot.CloseTimeUtc >= entryOpenedAtUtc)
            .Select(snapshot => (decimal?)snapshot.LowPrice)
            .ToList();

        if (candidateLows.Count == 0)
        {
            var interval = ResolveIntervalDuration(timeframe);
            var currentOpenTimeUtc = AlignToIntervalBoundary(timeProvider.GetUtcNow().UtcDateTime, timeframe);
            var lastClosedOpenTimeUtc = currentOpenTimeUtc - interval;
            var requestedStartOpenTimeUtc = AlignToIntervalBoundary(entryOpenedAtUtc, timeframe);
            var fallbackStartOpenTimeUtc = lastClosedOpenTimeUtc - ((optionsValue.PrimeHistoricalCandleCount - 1) * interval);
            var startOpenTimeUtc = requestedStartOpenTimeUtc > fallbackStartOpenTimeUtc
                ? requestedStartOpenTimeUtc
                : fallbackStartOpenTimeUtc;

            var historicalCandles = await historicalKlineClient.GetClosedCandlesAsync(
                symbol,
                timeframe,
                startOpenTimeUtc,
                lastClosedOpenTimeUtc,
                optionsValue.PrimeHistoricalCandleCount,
                cancellationToken);

            candidateLows.AddRange(historicalCandles
                .Where(snapshot => snapshot.IsClosed && snapshot.CloseTimeUtc >= entryOpenedAtUtc)
                .Select(snapshot => (decimal?)snapshot.LowPrice));
        }

        if (marketState.ReferencePrice.HasValue)
        {
            candidateLows.Add(marketState.ReferencePrice.Value);
        }

        return candidateLows
            .Where(value => value.HasValue && value.Value > 0m)
            .DefaultIfEmpty(null)
            .Min();
    }

    private static bool TryResolveUpperPriceBoundary(decimal basePrice, decimal percentage, out decimal thresholdPrice)
    {
        thresholdPrice = 0m;
        if (basePrice <= 0m || percentage <= 0m)
        {
            return false;
        }

        thresholdPrice = NormalizeDecimal(basePrice * (1m + (percentage / 100m)));
        return thresholdPrice > 0m;
    }

    private static bool TryResolveLowerPriceBoundary(decimal basePrice, decimal percentage, out decimal thresholdPrice)
    {
        thresholdPrice = 0m;
        if (basePrice <= 0m || percentage <= 0m)
        {
            return false;
        }

        thresholdPrice = NormalizeDecimal(basePrice * (1m - (percentage / 100m)));
        return thresholdPrice > 0m;
    }

    private static decimal? ResolveBollingerWidthPercentage(BollingerBandsSnapshot snapshot)
    {
        if (!snapshot.IsReady ||
            !snapshot.MiddleBand.HasValue ||
            !snapshot.UpperBand.HasValue ||
            !snapshot.LowerBand.HasValue ||
            snapshot.MiddleBand.Value == 0m)
        {
            return null;
        }

        return NormalizeDecimal(((snapshot.UpperBand.Value - snapshot.LowerBand.Value) / snapshot.MiddleBand.Value) * 100m);
    }

    private string BuildEntryRegimeLiveSummary(
        StrategyIndicatorSnapshot indicatorSnapshot,
        StrategyTradeDirection entryDirection,
        decimal? referencePrice,
        decimal? bollingerWidthPercentage,
        decimal regimeMiddleBandDislocationPercentage)
    {
        var parts = new List<string>();

        parts.Add($"RSI={FormatRegimeMetric(indicatorSnapshot.Rsi.IsReady ? indicatorSnapshot.Rsi.Value : null, "0.##")}");
        parts.Add($"MACD hist={FormatRegimeMetric(indicatorSnapshot.Macd.IsReady ? indicatorSnapshot.Macd.Histogram : null, "0.####")}");
        parts.Add($"Bollinger width={FormatRegimeMetric(bollingerWidthPercentage, "0.####", suffix: "%")}");

        if (referencePrice.HasValue &&
            indicatorSnapshot.Bollinger.IsReady &&
            indicatorSnapshot.Bollinger.MiddleBand.HasValue &&
            indicatorSnapshot.Bollinger.MiddleBand.Value > 0m &&
            regimeMiddleBandDislocationPercentage > 0m)
        {
            decimal middleBandDislocation = entryDirection == StrategyTradeDirection.Short
                ? NormalizeDecimal(((indicatorSnapshot.Bollinger.MiddleBand.Value - referencePrice.Value) / indicatorSnapshot.Bollinger.MiddleBand.Value) * 100m)
                : NormalizeDecimal(((referencePrice.Value - indicatorSnapshot.Bollinger.MiddleBand.Value) / indicatorSnapshot.Bollinger.MiddleBand.Value) * 100m);

            parts.Add($"Price vs middle band={middleBandDislocation.ToString("0.####", CultureInfo.InvariantCulture)}%");
        }

        return string.Join(" · ", parts);
    }

    private static string FormatRegimeMetric(decimal? value, string format, string suffix = "")
    {
        return value.HasValue
            ? value.Value.ToString(format, CultureInfo.InvariantCulture) + suffix
            : "n/a";
    }

    private async Task<PilotDispatchPlan> ResolvePilotDispatchPlanAsync(
        string ownerUserId,
        Guid exchangeAccountId,
        Guid botId,
        StrategySignalSnapshot signal,
        SymbolMetadataSnapshot symbolMetadata,
        decimal referencePrice,
        ExecutionEnvironment executionDispatchMode,
        CancellationToken cancellationToken)
    {
        if (signal.SignalType == StrategySignalType.Entry)
        {
            var entryDirection = ResolveSignalDirection(signal);
            return new PilotDispatchPlan(
                ResolveEntrySide(entryDirection),
                ResolvePilotQuantity(symbolMetadata, referencePrice),
                ReduceOnly: false);
        }

        var currentNetQuantity = await ResolveCurrentNetQuantityAsync(
            ownerUserId,
            exchangeAccountId,
            botId,
            signal.Symbol,
            executionDispatchMode,
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
        Guid botId,
        string symbol,
        ExecutionEnvironment executionDispatchMode,
        CancellationToken cancellationToken)
    {
        var normalizedSymbol = NormalizePositionSymbol(symbol);

        if (UsesInternalDemoExecution(executionDispatchMode))
        {
            return await dbContext.DemoPositions
                .AsNoTracking()
                .IgnoreQueryFilters()
                .Where(entity =>
                    entity.OwnerUserId == ownerUserId &&
                    entity.BotId == botId &&
                    entity.Symbol == normalizedSymbol &&
                    !entity.IsDeleted)
                .SumAsync(entity => entity.Quantity, cancellationToken);
        }

        return await LivePositionTruthResolver.ResolveNetQuantityAsync(
            dbContext,
            ownerUserId,
            ExchangeDataPlane.Futures,
            exchangeAccountId,
            normalizedSymbol,
            cancellationToken);
    }

    private bool UsesInternalDemoExecution(ExecutionEnvironment executionDispatchMode)
    {
        return executionDispatchMode == ExecutionEnvironment.Demo &&
            executionRuntimeOptionsValue.AllowInternalDemoExecution;
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
        leverage = NormalizeConfiguredLeverage(bot.Leverage ?? 1m);
        marginType = string.IsNullOrWhiteSpace(bot.MarginType)
            ? "ISOLATED"
            : bot.MarginType.Trim().ToUpperInvariant();
        failureCode = null;

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

    private decimal ResolvePilotQuantity(
        SymbolMetadataSnapshot symbolMetadata,
        decimal referencePrice)
    {
        return ResolvePilotQuantity(
            symbolMetadata,
            referencePrice,
            ResolveProtectedMinNotional(symbolMetadata.MinNotional));
    }

    private static decimal ResolvePilotQuantity(
        SymbolMetadataSnapshot symbolMetadata,
        decimal referencePrice,
        decimal? protectedMinNotional)
    {
        if (referencePrice <= 0m)
        {
            throw new ExecutionValidationException(
                EntryQuantitySizingFailedDecisionCode,
                $"Execution blocked because entry quantity sizing requires a positive reference price for '{symbolMetadata.Symbol}'.");
        }

        var candidateQuantity = symbolMetadata.MinQuantity
            ?? symbolMetadata.StepSize;

        if (candidateQuantity <= 0m)
        {
            throw new ExecutionValidationException(
                EntryQuantitySizingFailedDecisionCode,
                $"Execution blocked because entry quantity sizing could not be resolved for '{symbolMetadata.Symbol}'.");
        }

        if (protectedMinNotional is decimal minNotionalFloor)
        {
            candidateQuantity = Math.Max(candidateQuantity, minNotionalFloor / referencePrice);
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

        if (protectedMinNotional is decimal adjustedMinNotional &&
            (candidateQuantity * referencePrice) < adjustedMinNotional)
        {
            candidateQuantity = AlignUp(adjustedMinNotional / referencePrice, symbolMetadata.StepSize);
        }

        if (candidateQuantity <= 0m)
        {
            throw new ExecutionValidationException(
                EntryQuantitySizingFailedDecisionCode,
                $"Execution blocked because entry quantity sizing resolved to a non-positive value for '{symbolMetadata.Symbol}'.");
        }

        if (protectedMinNotional is decimal finalMinNotional &&
            (candidateQuantity * referencePrice) < finalMinNotional)
        {
            throw new ExecutionValidationException(
                EntryNotionalSafetyBlockedDecisionCode,
                $"Execution blocked because protected entry notional {(candidateQuantity * referencePrice):0.########} is below the protected minimum notional {finalMinNotional:0.########} for '{symbolMetadata.Symbol}'.");
        }

        return candidateQuantity;
    }

    private bool TryResolveDispatchSafetyBlock(
        StrategySignalSnapshot signal,
        SymbolMetadataSnapshot symbolMetadata,
        decimal referencePrice,
        PilotDispatchPlan dispatchPlan,
        out string? decisionReasonCode,
        out string? decisionSummary)
    {
        decisionReasonCode = null;
        decisionSummary = null;

        var orderNotional = dispatchPlan.Quantity * referencePrice;

        if (signal.SignalType == StrategySignalType.Entry)
        {
            var protectedMinNotional = ResolveProtectedMinNotional(symbolMetadata.MinNotional);

            if (protectedMinNotional is decimal requiredMinNotional && orderNotional < requiredMinNotional)
            {
                decisionReasonCode = EntryNotionalSafetyBlockedDecisionCode;
                decisionSummary = $"Entry signal was skipped because protected entry notional {orderNotional:0.########} is below the protected minimum notional {requiredMinNotional:0.########} for {signal.Symbol}.";
                return true;
            }

            if (optionsValue.TryResolveMaxPilotOrderNotional(out var maxPilotOrderNotional) &&
                orderNotional > maxPilotOrderNotional)
            {
                decisionReasonCode = EntryQuantitySizingFailedDecisionCode;
                decisionSummary = $"Entry signal was skipped because safe entry sizing for {signal.Symbol} requires notional {orderNotional:0.########}, which exceeds the configured pilot order notional cap {maxPilotOrderNotional:0.########}.";
                return true;
            }

            return false;
        }

        return false;
    }

    private async Task<OrderExecutionBreakerSnapshot?> ResolveActiveOrderExecutionBreakerAsync(
        CancellationToken cancellationToken)
    {
        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;

        var breaker = await dbContext.DependencyCircuitBreakerStates
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(entity =>
                !entity.IsDeleted &&
                entity.BreakerKind == DependencyCircuitBreakerKind.OrderExecution &&
                entity.StateCode != CircuitBreakerStateCode.Closed)
            .OrderByDescending(entity => entity.CooldownUntilUtc)
            .ThenByDescending(entity => entity.LastFailureAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

        if (breaker is null)
        {
            return null;
        }

        var isActive = breaker.StateCode is CircuitBreakerStateCode.Cooldown or CircuitBreakerStateCode.HalfOpen or CircuitBreakerStateCode.Degraded or CircuitBreakerStateCode.Retrying;
        if (!isActive)
        {
            return null;
        }

        if (breaker.CooldownUntilUtc.HasValue && breaker.CooldownUntilUtc.Value <= nowUtc && breaker.StateCode == CircuitBreakerStateCode.Cooldown)
        {
            return null;
        }

        return new OrderExecutionBreakerSnapshot(
            breaker.StateCode,
            breaker.CooldownUntilUtc,
            breaker.LastErrorCode);
    }

    private static bool TryResolveCooldownSkip(
        UserExecutionOverrideEvaluationResult evaluationResult,
        out string? decisionReasonCode,
        out string? decisionSummary)
    {
        decisionReasonCode = null;
        decisionSummary = null;

        if (!evaluationResult.IsBlocked)
        {
            return false;
        }

        if (string.Equals(evaluationResult.BlockCode, "UserExecutionBotCooldownActive", StringComparison.Ordinal))
        {
            decisionReasonCode = BotCooldownActiveDecisionCode;
            decisionSummary = "Execution signal was skipped because bot cooldown is still active.";
            return true;
        }

        if (string.Equals(evaluationResult.BlockCode, "UserExecutionSymbolCooldownActive", StringComparison.Ordinal))
        {
            decisionReasonCode = SymbolCooldownActiveDecisionCode;
            decisionSummary = "Execution signal was skipped because symbol cooldown is still active.";
            return true;
        }

        return false;
    }

    private decimal? ResolveProtectedMinNotional(decimal? minNotional)
    {
        if (!minNotional.HasValue || minNotional.Value <= 0m)
        {
            return null;
        }

        var multiplier = optionsValue.MinNotionalSafetyMultiplier < 1m
            ? 1m
            : optionsValue.MinNotionalSafetyMultiplier;

        return decimal.Round(minNotional.Value * multiplier, 8, MidpointRounding.AwayFromZero);
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
        var strategyTradeDirection = signalGenerationResult.EvaluationResult.Direction;
        var strategyDirection = ResolveStrategyDirection(signalGenerationResult.EvaluationResult);
        var aiEvaluation = ResolvePrimaryAiEvaluation(signalGenerationResult);
        var duplicateSuppressed = signalGenerationResult.SuppressedDuplicateCount > 0 && signalGenerationResult.Signals.Count == 0;
        var hasEntryIntent = signal is { SignalType: StrategySignalType.Entry } ||
                             (signal is null &&
                              aiEvaluation?.IsFallback == true &&
                              IsActionableDirection(strategyDirection));
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
            strategyTradeDirection,
            shouldEvaluate: hasEntryIntent &&
                            IsActionableDirection(strategyDirection) &&
                            !duplicateSuppressed,
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
                ResolveAgreementState(strategyDirection, aiEvaluation),
                aiEvaluation?.AdvisoryScore ?? 0m,
                ResolveAiContributionSummary(aiEvaluation)),
            cancellationToken);
    }

    private async Task TryEnsureAiShadowOutcomeCoverageAsync(string ownerUserId, CancellationToken cancellationToken)
    {
        try
        {
            await aiShadowDecisionService.EnsureOutcomeCoverageAsync(
                ownerUserId,
                take: AiShadowOutcomeCoverageBatchSize,
                cancellationToken: cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                exception,
                "Bot execution pilot ignored AI shadow outcome coverage failure. BatchSize={BatchSize}.",
                AiShadowOutcomeCoverageBatchSize);
        }
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
        StrategyTradeDirection strategyDirection,
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
                ResolveEntrySide(strategyDirection),
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
                Plane: ExchangeDataPlane.Futures),
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


    private static string ResolveSameDirectionEntrySuppressedDecisionCode(StrategyTradeDirection entryDirection)
    {
        return entryDirection switch
        {
            StrategyTradeDirection.Long => "SameDirectionLongEntrySuppressed",
            StrategyTradeDirection.Short => "SameDirectionShortEntrySuppressed",
            _ => "SameDirectionEntrySuppressed"
        };
    }

    private static string ResolveEntryRegimeFilterBlockedDecisionCode(StrategyTradeDirection entryDirection)
    {
        return entryDirection switch
        {
            StrategyTradeDirection.Long => "LongEntryRegimeFilterBlocked",
            StrategyTradeDirection.Short => "ShortEntryRegimeFilterBlocked",
            _ => "EntryRegimeFilterBlocked"
        };
    }

    private static string ResolveEntryHysteresisActiveDecisionCode(StrategyTradeDirection entryDirection)
    {
        return entryDirection switch
        {
            StrategyTradeDirection.Long => "LongEntryHysteresisActive",
            StrategyTradeDirection.Short => "ShortEntryHysteresisActive",
            _ => "EntryHysteresisActive"
        };
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
        CancellationToken cancellationToken,
        string? positionAdoptionSummary = null,
        decimal? requestedQuantity = null,
        decimal? referencePrice = null)
    {
        var isExitCloseOnly = IsExitCloseOnlyDecision(decisionReasonCode, decisionSummary);
        decisionSummary = AppendExitReasonToken(decisionSummary, decisionReasonCode, isExitCloseOnly);
        decisionSummary = AppendSignalClassificationDecisionSummary(
            decisionSummary,
            signal.SignalType,
            decisionReasonCode,
            isExitCloseOnly);
        decisionSummary = AppendPositionAdoptionSummary(decisionSummary, positionAdoptionSummary);

        decimal? requestedNotional = requestedQuantity.HasValue && referencePrice.HasValue
            ? requestedQuantity.Value * referencePrice.Value
            : (decimal?)null;

        var snapshotJson = JsonSerializer.Serialize(new
        {
            PreviousDecisionOutcome = previousDecisionTrace?.DecisionOutcome,
            PreviousDecisionReasonCode = previousDecisionTrace?.DecisionReasonCode,
            SignalType = signal.SignalType.ToString(),
            SignalId = signal.StrategySignalId,
            Symbol = signal.Symbol,
            Timeframe = signal.Timeframe,
            ExchangeEnvironment = signal.Mode.ToString(),
            NetQuantity = netQuantity,
            RequestedQuantity = requestedQuantity,
            ReferencePrice = referencePrice,
            RequestedNotional = requestedNotional
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
                DecisionAtUtc: timeProvider.GetUtcNow().UtcDateTime,
                CreatedAtUtc: timeProvider.GetUtcNow().UtcDateTime.AddTicks(1)),
            cancellationToken);
    }

    private async Task WriteEntrySkippedDecisionTraceAsync(
        string ownerUserId,
        TradingStrategyVersion publishedVersion,
        StrategySignalSnapshot signal,
        string correlationId,
        DecisionTrace? previousDecisionTrace,
        string decisionSummary,
        decimal netQuantity,
        CancellationToken cancellationToken,
        string decisionReasonCode = "EntrySuppressed",
        string? positionAdoptionSummary = null,
        decimal? requestedQuantity = null,
        decimal? referencePrice = null)
    {
        decisionSummary = AppendSignalClassificationDecisionSummary(
            decisionSummary,
            signal.SignalType,
            decisionReasonCode,
            isExitCloseOnly: false);
        decisionSummary = AppendPositionAdoptionSummary(decisionSummary, positionAdoptionSummary);

        decimal? requestedNotional = requestedQuantity.HasValue && referencePrice.HasValue
            ? requestedQuantity.Value * referencePrice.Value
            : (decimal?)null;

        var snapshotJson = JsonSerializer.Serialize(new
        {
            PreviousDecisionOutcome = previousDecisionTrace?.DecisionOutcome,
            PreviousDecisionReasonCode = previousDecisionTrace?.DecisionReasonCode,
            SignalType = signal.SignalType.ToString(),
            SignalId = signal.StrategySignalId,
            Symbol = signal.Symbol,
            Timeframe = signal.Timeframe,
            ExchangeEnvironment = signal.Mode.ToString(),
            NetQuantity = netQuantity,
            RequestedQuantity = requestedQuantity,
            ReferencePrice = referencePrice,
            RequestedNotional = requestedNotional
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
                DecisionAtUtc: timeProvider.GetUtcNow().UtcDateTime,
                CreatedAtUtc: timeProvider.GetUtcNow().UtcDateTime.AddTicks(1)),
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

    private static bool TryResolveEntryDirectionModeBlock(
        TradingBot bot,
        StrategyTradeDirection entryDirection,
        out string? summary)
    {
        summary = null;

        if (!IsActionableDirection(entryDirection))
        {
            return false;
        }

        var blocked = bot.DirectionMode switch
        {
            TradingBotDirectionMode.LongOnly => entryDirection == StrategyTradeDirection.Short,
            TradingBotDirectionMode.ShortOnly => entryDirection == StrategyTradeDirection.Long,
            _ => false
        };

        if (!blocked)
        {
            return false;
        }

        summary = $"Entry signal was skipped because bot direction mode {bot.DirectionMode} does not allow {entryDirection.ToString().ToLowerInvariant()} entries for {bot.Symbol}.";
        return true;
    }

    private static string ResolveStrategyDirection(StrategyEvaluationResult evaluationResult)
    {
        return evaluationResult.Direction.ToString();
    }


    private static StrategyTradeDirection ResolveSignalDirection(StrategySignalSnapshot signal)
    {
        return signal.ExplainabilityPayload.RuleResultSnapshot.Direction;
    }

    private static ExecutionOrderSide ResolveEntrySide(StrategyTradeDirection direction)
    {
        return direction switch
        {
            StrategyTradeDirection.Long => ExecutionOrderSide.Buy,
            StrategyTradeDirection.Short => ExecutionOrderSide.Sell,
            _ => throw new ExecutionValidationException(
                "UnsupportedEntryDirection",
                "Execution blocked because entry direction was not actionable.")
        };
    }

    private static bool IsActionableDirection(StrategyTradeDirection direction)
    {
        return direction is StrategyTradeDirection.Long or StrategyTradeDirection.Short;
    }

    private static bool IsActionableDirection(string strategyDirection)
    {
        return string.Equals(strategyDirection, "Long", StringComparison.Ordinal) ||
               string.Equals(strategyDirection, "Short", StringComparison.Ordinal);
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

        if (string.Equals(providerName, ShadowLinearAiSignalProviderAdapter.ProviderNameValue, StringComparison.OrdinalIgnoreCase))
        {
            return "shadow-linear-v1";
        }

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

    private static string? ResolveAiContributionSummary(AiSignalEvaluationResult? aiEvaluation)
    {
        if (aiEvaluation?.Contributions is null || aiEvaluation.Contributions.Count == 0)
        {
            return null;
        }

        return Truncate(
            string.Join(
                " | ",
                aiEvaluation.Contributions
                    .OrderByDescending(item => Math.Abs(item.Contribution))
                    .ThenBy(item => item.Code, StringComparer.Ordinal)
                    .Take(4)
                    .Select(item => $"{item.Code} {FormatSignedDecimal(item.Contribution)}")),
            1024);
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

    private static string FormatSignedDecimal(decimal value)
    {
        return value > 0m
            ? $"+{value:0.###}"
            : $"{value:0.###}";
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

        if (!IsActionableDirection(strategyDirection))
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
               string.Equals(blockCode, "RiskConcurrencyMaxOpenPositionsExceeded", StringComparison.Ordinal) ||
               string.Equals(blockCode, "RiskConcurrencyMaxPendingOrdersExceeded", StringComparison.Ordinal) ||
               string.Equals(blockCode, "RiskConcurrencyMaxSymbolExposureExceeded", StringComparison.Ordinal) ||
               string.Equals(blockCode, "RiskConcurrencyMaxSymbolsExceeded", StringComparison.Ordinal) ||
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

    private static string BuildPilotExecutionContext(
        string marginType,
        decimal leverage,
        bool pilotActivationEnabled,
        ExecutionEnvironment executionDispatchMode = ExecutionEnvironment.Live,
        string? leveragePolicySummary = null)
    {
        var leverageContext = string.IsNullOrWhiteSpace(leveragePolicySummary)
            ? string.Empty
            : $" | {leveragePolicySummary}";
        if (executionDispatchMode == ExecutionEnvironment.Demo)
        {
            return FormattableString.Invariant(
                $"ControlledDemoBootstrap=True | ExecutionDispatchMode=Demo | PilotActivationEnabled={pilotActivationEnabled} | PilotMarginType={marginType} | PilotLeverage={leverage:0.########}{leverageContext}");
        }

        return FormattableString.Invariant(
            $"DevelopmentFuturesTestnetPilot=True | PilotActivationEnabled={pilotActivationEnabled} | PilotMarginType={marginType} | PilotLeverage={leverage:0.########}{leverageContext}");
    }

    private PilotLeveragePolicyEvaluation EvaluatePilotLeveragePolicy(
        TradingBot bot,
        PilotDispatchPlan dispatchPlan)
    {
        var requestedLeverage = bot.Leverage ?? 1m;
        var effectiveLeverage = NormalizeConfiguredLeverage(requestedLeverage);
        var leverageSource = bot.Leverage.HasValue ? "Bot" : "Default";
        var maxAllowedLeverage = ResolveEffectiveMaxAllowedLeverage(bot, effectiveLeverage);

        if (dispatchPlan.ReduceOnly)
        {
            return new PilotLeveragePolicyEvaluation(
                IsBlocked: false,
                RequestedLeverage: requestedLeverage,
                EffectiveLeverage: effectiveLeverage,
                MaxAllowedLeverage: maxAllowedLeverage,
                LeverageSource: leverageSource,
                Decision: "Skipped",
                ReasonCode: LeverageAlignmentSkippedForReduceOnlyReasonCode,
                AlignmentSkippedForReduceOnly: true,
                Summary: BuildLeveragePolicySummary(
                    requestedLeverage,
                    effectiveLeverage,
                    maxAllowedLeverage,
                    leverageSource,
                    "Skipped",
                    LeverageAlignmentSkippedForReduceOnlyReasonCode,
                    alignmentSkippedForReduceOnly: true));
        }

        if (effectiveLeverage > maxAllowedLeverage)
        {
            return new PilotLeveragePolicyEvaluation(
                IsBlocked: true,
                RequestedLeverage: requestedLeverage,
                EffectiveLeverage: effectiveLeverage,
                MaxAllowedLeverage: maxAllowedLeverage,
                LeverageSource: leverageSource,
                Decision: "Blocked",
                ReasonCode: LeveragePolicyExceededDecisionCode,
                AlignmentSkippedForReduceOnly: false,
                Summary: BuildLeveragePolicySummary(
                    requestedLeverage,
                    effectiveLeverage,
                    maxAllowedLeverage,
                    leverageSource,
                    "Blocked",
                    LeveragePolicyExceededDecisionCode,
                    alignmentSkippedForReduceOnly: false));
        }

        return new PilotLeveragePolicyEvaluation(
            IsBlocked: false,
            RequestedLeverage: requestedLeverage,
            EffectiveLeverage: effectiveLeverage,
            MaxAllowedLeverage: maxAllowedLeverage,
            LeverageSource: leverageSource,
            Decision: "Allowed",
            ReasonCode: LeveragePolicyAllowedReasonCode,
            AlignmentSkippedForReduceOnly: false,
            Summary: BuildLeveragePolicySummary(
                requestedLeverage,
                effectiveLeverage,
                maxAllowedLeverage,
                leverageSource,
                "Allowed",
                LeveragePolicyAllowedReasonCode,
                alignmentSkippedForReduceOnly: false));
    }

    private SymbolExecutionAllowlistEvaluation EvaluateSymbolExecutionAllowlist(
        string symbol,
        bool reduceOnly)
    {
        if (!optionsValue.TryResolveNormalizedAllowedExecutionSymbols(out var allowedExecutionSymbols))
        {
            return SymbolExecutionAllowlistEvaluation.NotConfigured;
        }

        var normalizedSymbol = MarketDataSymbolNormalizer.Normalize(symbol);
        var allowedExecutionSymbolsSummary = allowedExecutionSymbols.Length == 0
            ? "none"
            : string.Join(",", allowedExecutionSymbols);

        if (reduceOnly)
        {
            return new SymbolExecutionAllowlistEvaluation(
                IsConfigured: true,
                IsBlocked: false,
                Decision: "SkippedForCloseOnly",
                ReasonCode: SymbolAllowlistSkippedForCloseOnlyReasonCode,
                Summary: BuildSymbolExecutionAllowlistSummary(
                    normalizedSymbol,
                    allowedExecutionSymbolsSummary,
                    "SkippedForCloseOnly",
                    SymbolAllowlistSkippedForCloseOnlyReasonCode));
        }

        if (allowedExecutionSymbols.Length == 0)
        {
            return new SymbolExecutionAllowlistEvaluation(
                IsConfigured: true,
                IsBlocked: true,
                Decision: "Blocked",
                ReasonCode: SymbolAllowlistEmptyDecisionCode,
                Summary: BuildSymbolExecutionAllowlistSummary(
                    normalizedSymbol,
                    allowedExecutionSymbolsSummary,
                    "Blocked",
                    SymbolAllowlistEmptyDecisionCode));
        }

        if (!allowedExecutionSymbols.Contains(normalizedSymbol, StringComparer.Ordinal))
        {
            return new SymbolExecutionAllowlistEvaluation(
                IsConfigured: true,
                IsBlocked: true,
                Decision: "Blocked",
                ReasonCode: SymbolExecutionNotAllowedDecisionCode,
                Summary: BuildSymbolExecutionAllowlistSummary(
                    normalizedSymbol,
                    allowedExecutionSymbolsSummary,
                    "Blocked",
                    SymbolExecutionNotAllowedDecisionCode));
        }

        return new SymbolExecutionAllowlistEvaluation(
            IsConfigured: true,
            IsBlocked: false,
            Decision: "Allowed",
            ReasonCode: SymbolAllowlistAllowedReasonCode,
            Summary: BuildSymbolExecutionAllowlistSummary(
                normalizedSymbol,
                allowedExecutionSymbolsSummary,
                "Allowed",
                SymbolAllowlistAllowedReasonCode));
    }

    private decimal ResolveEffectiveMaxAllowedLeverage(TradingBot bot, decimal effectiveLeverage)
    {
        var configuredMaxAllowedLeverage = NormalizeConfiguredLeverage(optionsValue.MaxAllowedLeverage);
        return effectiveLeverage > configuredMaxAllowedLeverage &&
               IsClockDriftSmokeLeverageAllowed(bot, effectiveLeverage)
            ? effectiveLeverage
            : configuredMaxAllowedLeverage;
    }

    private static decimal NormalizeConfiguredLeverage(decimal leverage)
    {
        var normalizedLeverage = decimal.Truncate(leverage);
        return normalizedLeverage < 1m
            ? 1m
            : normalizedLeverage;
    }

    private static string BuildLeveragePolicySummary(
        decimal requestedLeverage,
        decimal effectiveLeverage,
        decimal maxAllowedLeverage,
        string leverageSource,
        string decision,
        string reasonCode,
        bool alignmentSkippedForReduceOnly)
    {
        return FormattableString.Invariant(
            $"RequestedLeverage={requestedLeverage:0.########}; EffectiveLeverage={effectiveLeverage:0.########}; MaxAllowedLeverage={maxAllowedLeverage:0.########}; LeverageSource={leverageSource}; LeveragePolicyDecision={decision}; LeveragePolicyReason={reasonCode}; LeverageAlignmentSkippedForReduceOnly={alignmentSkippedForReduceOnly}");
    }

    private static string BuildSymbolExecutionAllowlistSummary(
        string symbol,
        string allowedExecutionSymbolsSummary,
        string decision,
        string reasonCode)
    {
        return $"SymbolExecutionAllowlist=Applied; AllowedExecutionSymbols={allowedExecutionSymbolsSummary}; SymbolAllowlistDecision={decision}; SymbolAllowlistReason={reasonCode}; SelectedSymbol={symbol}";
    }

    private static string AppendPilotContextSegment(string context, string? segment)
    {
        return string.IsNullOrWhiteSpace(segment)
            ? context
            : $"{context} | {segment}";
    }

    private static string? ExtractConcurrencyGuardSummary(string? guardSummary)
    {
        if (string.IsNullOrWhiteSpace(guardSummary))
        {
            return null;
        }

        string[] allowedPrefixes =
        [
            "ConcurrencyPolicy=",
            "MaxOpenPositionsPerUser=",
            "CurrentOpenPositionsPerUser=",
            "MaxOpenPositionsGlobal=",
            "CurrentOpenPositionsGlobal=",
            "MaxOpenPositionsPerSymbol=",
            "CurrentOpenPositionsPerSymbol=",
            "MaxPendingOrdersPerUser=",
            "CurrentPendingOrdersPerUser=",
            "MaxConcurrentEntryOrdersPerUser=",
            "CurrentConcurrentEntryOrdersPerUser=",
            "MaxConcurrentEntryOrdersPerSymbol=",
            "CurrentConcurrentEntryOrdersPerSymbol=",
            "MaxSymbolsWithOpenPositionPerUser=",
            "CurrentSymbolsWithOpenPositionPerUser=",
            "ConcurrencyDecision=",
            "ConcurrencyReason=",
            "ConcurrencyLimitSkippedForCloseOnly="
        ];

        var filteredTokens = guardSummary
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(token => allowedPrefixes.Any(prefix => token.StartsWith(prefix, StringComparison.Ordinal)))
            .ToArray();

        return filteredTokens.Length == 0
            ? null
            : string.Join("; ", filteredTokens);
    }

    private static string PrependDecisionSummarySegment(string? segment, string summary)
    {
        return string.IsNullOrWhiteSpace(segment)
            ? summary
            : $"{segment}; {summary}";
    }

    private static string AppendExecutionIntentContext(
        string baseContext,
        StrategySignalType signalType,
        CloseOnlyExecutionIntent? closeOnlyIntent,
        PilotDispatchPlan dispatchPlan)
    {
        if (closeOnlyIntent is null)
        {
            if (signalType != StrategySignalType.Entry)
            {
                return baseContext;
            }

            return FormattableString.Invariant(
                $"{baseContext} | SignalType=Entry | ExecutionIntent=Entry | ExitIntent=n/a | EntrySource=StrategyEntry | ExitSource=n/a | ReverseEntryConvertedToCloseOnly=False | ManualClose=False");
        }

        var positionAdoptionSummary = string.IsNullOrWhiteSpace(closeOnlyIntent.PositionAdoptionSummary)
            ? string.Empty
            : $" | {ConvertSummaryTokensToExecutionContextSegment(closeOnlyIntent.PositionAdoptionSummary)}";
        var exitPnlGuardSummary = string.IsNullOrWhiteSpace(closeOnlyIntent.ExitPnlGuardSummary)
            ? string.Empty
            : $" | {closeOnlyIntent.ExitPnlGuardSummary}";

        return FormattableString.Invariant(
            $"{baseContext} | SignalType=Reverse | ExecutionIntent={ExitCloseOnlyIntentCode} | ExitIntent={ExitCloseOnlyIntentCode} | EntrySource=StrategyEntry | ExitSource={ExitReasonReverseSignal} | ReverseEntryConvertedToCloseOnly=True | ManualClose=False | OpenPositionQuantity={closeOnlyIntent.OpenPositionQuantity:0.########} | CloseQuantity={dispatchPlan.Quantity:0.########} | CloseSide={closeOnlyIntent.CloseSide} | ReduceOnly={dispatchPlan.ReduceOnly} | AutoReverse=False{positionAdoptionSummary}{exitPnlGuardSummary}");
    }

    private CloseOnlyPnlGuardEvaluation EvaluateExitCloseOnlyPnlGuard(
        string symbol,
        CurrentPositionSnapshot? currentPosition,
        SymbolMetadataSnapshot symbolMetadata,
        decimal? referencePrice,
        string? positionAdoptionSummary)
    {
        var positionDirection = currentPosition?.Direction ?? StrategyTradeDirection.Neutral;
        var minimumProfitPct = ResolveReverseExitMinimumProfitPct();
        var exitOnReverseSignalOnlyIfProfitable = optionsValue.ExitOnReverseSignalOnlyIfProfitable;
        decimal closeQuantity;
        try
        {
            closeQuantity = ResolvePilotExitQuantity(
                symbolMetadata,
                Math.Abs(currentPosition?.NetQuantity ?? 0m));
        }
        catch (ExecutionValidationException)
        {
            var invalidSummary = AppendPositionAdoptionSummary(
                BuildExitCloseOnlyPnlGuardSummary(
                exitPnlGuardState: "Blocked",
                reasonCode: ExitCloseOnlyBlockedQuantityInvalidDecisionCode,
                positionDirection,
                currentPosition?.EntryPrice,
                referencePrice,
                currentPosition?.Direction == StrategyTradeDirection.Long ? ExecutionOrderSide.Sell : ExecutionOrderSide.Buy,
                openPositionQuantity: currentPosition?.NetQuantity,
                closeQuantity: null,
                estimatedPnlQuote: null,
                estimatedPnlPct: null,
                minimumProfitPct: minimumProfitPct,
                exitPolicyDecision: "Blocked",
                exitPolicyReason: ExitCloseOnlyBlockedQuantityInvalidDecisionCode,
                exitOnReverseSignalOnlyIfProfitable: exitOnReverseSignalOnlyIfProfitable),
                positionAdoptionSummary);
            return new CloseOnlyPnlGuardEvaluation(
                IsBlocked: true,
                DecisionReasonCode: ExitCloseOnlyBlockedQuantityInvalidDecisionCode,
                DecisionSummary: invalidSummary,
                GuardSummary: invalidSummary,
                CloseQuantity: null);
        }

        var entryPrice = currentPosition?.EntryPrice ?? 0m;
        var exitPrice = referencePrice ?? 0m;
        if (currentPosition is null || currentPosition.NetQuantity == 0m || entryPrice <= 0m || exitPrice <= 0m)
        {
            var invalidSummary = AppendPositionAdoptionSummary(
                BuildExitCloseOnlyPnlGuardSummary(
                exitPnlGuardState: "Blocked",
                reasonCode: ExitCloseOnlyBlockedQuantityInvalidDecisionCode,
                positionDirection,
                currentPosition?.EntryPrice,
                referencePrice,
                currentPosition?.Direction == StrategyTradeDirection.Long ? ExecutionOrderSide.Sell : ExecutionOrderSide.Buy,
                openPositionQuantity: currentPosition?.NetQuantity,
                closeQuantity: closeQuantity,
                estimatedPnlQuote: null,
                estimatedPnlPct: null,
                minimumProfitPct: minimumProfitPct,
                exitPolicyDecision: "Blocked",
                exitPolicyReason: ExitCloseOnlyBlockedQuantityInvalidDecisionCode,
                exitOnReverseSignalOnlyIfProfitable: exitOnReverseSignalOnlyIfProfitable),
                positionAdoptionSummary);
            return new CloseOnlyPnlGuardEvaluation(
                IsBlocked: true,
                DecisionReasonCode: ExitCloseOnlyBlockedQuantityInvalidDecisionCode,
                DecisionSummary: invalidSummary,
                GuardSummary: invalidSummary,
                CloseQuantity: closeQuantity);
        }

        var estimatedPnlQuote = currentPosition.Direction == StrategyTradeDirection.Long
            ? NormalizeDecimal((exitPrice - entryPrice) * Math.Abs(closeQuantity))
            : NormalizeDecimal((entryPrice - exitPrice) * Math.Abs(closeQuantity));
        var estimatedPnlPct = currentPosition.Direction == StrategyTradeDirection.Long
            ? NormalizeDecimal(((exitPrice - entryPrice) / entryPrice) * 100m)
            : NormalizeDecimal(((entryPrice - exitPrice) / entryPrice) * 100m);

        if (exitOnReverseSignalOnlyIfProfitable &&
            (estimatedPnlQuote < 0m || estimatedPnlPct < minimumProfitPct))
        {
            var decisionReasonCode = currentPosition.Direction == StrategyTradeDirection.Long
                ? ExitCloseOnlyBlockedUnprofitableLongDecisionCode
                : ExitCloseOnlyBlockedUnprofitableShortDecisionCode;
            var blockedSummary = AppendPositionAdoptionSummary(
                BuildExitCloseOnlyPnlGuardSummary(
                exitPnlGuardState: "Blocked",
                reasonCode: decisionReasonCode,
                currentPosition.Direction,
                entryPrice,
                exitPrice,
                currentPosition.Direction == StrategyTradeDirection.Long ? ExecutionOrderSide.Sell : ExecutionOrderSide.Buy,
                openPositionQuantity: currentPosition.NetQuantity,
                closeQuantity: closeQuantity,
                estimatedPnlQuote,
                estimatedPnlPct,
                minimumProfitPct: minimumProfitPct,
                exitPolicyDecision: "Blocked",
                exitPolicyReason: ReverseExitBlockedUnprofitablePolicyReason,
                exitOnReverseSignalOnlyIfProfitable: exitOnReverseSignalOnlyIfProfitable),
                positionAdoptionSummary);
            return new CloseOnlyPnlGuardEvaluation(
                IsBlocked: true,
                DecisionReasonCode: decisionReasonCode,
                DecisionSummary: blockedSummary,
                GuardSummary: blockedSummary,
                CloseQuantity: closeQuantity);
        }

        return new CloseOnlyPnlGuardEvaluation(
            IsBlocked: false,
            DecisionReasonCode: ExitCloseOnlyAllowedTakeProfitDecisionCode,
            DecisionSummary: AppendPositionAdoptionSummary(
                BuildExitCloseOnlyPnlGuardSummary(
                exitPnlGuardState: "Allowed",
                reasonCode: ExitCloseOnlyAllowedTakeProfitDecisionCode,
                currentPosition.Direction,
                entryPrice,
                exitPrice,
                currentPosition.Direction == StrategyTradeDirection.Long ? ExecutionOrderSide.Sell : ExecutionOrderSide.Buy,
                openPositionQuantity: currentPosition.NetQuantity,
                closeQuantity: closeQuantity,
                estimatedPnlQuote,
                estimatedPnlPct,
                minimumProfitPct: minimumProfitPct,
                exitPolicyDecision: "Allowed",
                exitPolicyReason: exitOnReverseSignalOnlyIfProfitable
                    ? ReverseSignalProfitablePolicyReason
                    : ReverseSignalProfitPolicyDisabledPolicyReason,
                exitOnReverseSignalOnlyIfProfitable: exitOnReverseSignalOnlyIfProfitable),
                positionAdoptionSummary),
            GuardSummary: AppendPositionAdoptionSummary(
                BuildExitCloseOnlyPnlGuardSummary(
                exitPnlGuardState: "Allowed",
                reasonCode: ExitCloseOnlyAllowedTakeProfitDecisionCode,
                currentPosition.Direction,
                entryPrice,
                exitPrice,
                currentPosition.Direction == StrategyTradeDirection.Long ? ExecutionOrderSide.Sell : ExecutionOrderSide.Buy,
                openPositionQuantity: currentPosition.NetQuantity,
                closeQuantity: closeQuantity,
                estimatedPnlQuote,
                estimatedPnlPct,
                minimumProfitPct: minimumProfitPct,
                exitPolicyDecision: "Allowed",
                exitPolicyReason: exitOnReverseSignalOnlyIfProfitable
                    ? ReverseSignalProfitablePolicyReason
                    : ReverseSignalProfitPolicyDisabledPolicyReason,
                exitOnReverseSignalOnlyIfProfitable: exitOnReverseSignalOnlyIfProfitable),
                positionAdoptionSummary),
            CloseQuantity: closeQuantity);
    }

    private async Task<PositionAdoptionResolution> ResolvePositionAdoptionAsync(
        TradingBot bot,
        ExchangeAccount exchangeAccount,
        string normalizedSymbol,
        decimal currentNetQuantity,
        CancellationToken cancellationToken)
    {
        var matchingBotCount = await dbContext.TradingBots
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(entity =>
                entity.OwnerUserId == bot.OwnerUserId &&
                entity.ExchangeAccountId == exchangeAccount.Id &&
                entity.Symbol == bot.Symbol &&
                !entity.IsDeleted)
            .CountAsync(cancellationToken);

        return matchingBotCount > 1
            ? PositionAdoptionResolution.Ambiguous(normalizedSymbol, currentNetQuantity, exchangeAccount.Id)
            : PositionAdoptionResolution.Adopted(
                normalizedSymbol,
                currentNetQuantity,
                exchangeAccount.Id,
                bot.Id,
                optionsValue.AutoManageAdoptedPositions);
    }

    private bool IsAutoManageAdoptedPositionEnabled(PositionAdoptionResolution positionAdoption)
    {
        return optionsValue.AutoManageAdoptedPositions &&
               string.Equals(positionAdoption.State, PositionAdoptionAdoptedState, StringComparison.Ordinal);
    }

    private static string AppendPositionAdoptionSummary(string summary, string? positionAdoptionSummary)
    {
        if (string.IsNullOrWhiteSpace(positionAdoptionSummary) ||
            summary.Contains("PositionAdoption=", StringComparison.Ordinal))
        {
            return summary;
        }

        foreach (var classificationMarker in new[]
                 {
                     "ManualClose=False;",
                     "ReverseEntryConvertedToCloseOnly=True;",
                     "ReverseEntryConvertedToCloseOnly=False;"
                 })
        {
            var markerIndex = summary.IndexOf(classificationMarker, StringComparison.Ordinal);
            if (markerIndex < 0)
            {
                continue;
            }

            var insertionIndex = markerIndex + classificationMarker.Length;
            var prefix = summary[..insertionIndex].TrimEnd();
            var suffix = summary[insertionIndex..].TrimStart();
            return string.IsNullOrWhiteSpace(suffix)
                ? $"{prefix} {positionAdoptionSummary}"
                : $"{prefix} {positionAdoptionSummary}; {suffix}";
        }

        return $"{positionAdoptionSummary}; {summary}";
    }

    private static string ConvertSummaryTokensToExecutionContextSegment(string summary)
    {
        return summary.Replace("; ", " | ", StringComparison.Ordinal);
    }

    private static string BuildPositionAdoptionSummary(
        string state,
        string symbol,
        decimal netQuantity,
        Guid exchangeAccountId,
        Guid? adoptedByBotId,
        string adoptionReason,
        bool autoManagementEnabled,
        string autoManagementReason)
    {
        var side = netQuantity > 0m ? "Long" : "Short";
        var adoptedPositionDetected = string.Equals(state, PositionAdoptionAdoptedState, StringComparison.Ordinal);
        var ambiguousPositionDetected = string.Equals(state, PositionAdoptionAmbiguousState, StringComparison.Ordinal);
        return FormattableString.Invariant(
            $"PositionAdoption={state}; AdoptedPositionSymbol={symbol}; AdoptedExchangeAccountId={exchangeAccountId:D}; AdoptedByBotId={(adoptedByBotId.HasValue ? adoptedByBotId.Value.ToString("D") : "n/a")}; AutoManagementEnabled={(autoManagementEnabled ? "True" : "False")}; AutoPositionManagement={(autoManagementEnabled ? "True" : "False")}; AutoManagementReason={autoManagementReason}; AdoptedPosition={(adoptedPositionDetected ? "True" : "False")}; OrphanPositionDetected=False; AmbiguousPositionDetected={(ambiguousPositionDetected ? "True" : "False")}; AdoptionReason={adoptionReason}; AdoptedPositionQuantity={netQuantity:0.########}; AdoptedPositionSide={side}");
    }

    private string BuildExitCloseOnlyPnlGuardSummary(
        string exitPnlGuardState,
        string reasonCode,
        StrategyTradeDirection positionDirection,
        decimal? entryPrice,
        decimal? exitPrice,
        ExecutionOrderSide closeSide,
        decimal? openPositionQuantity,
        decimal? closeQuantity,
        decimal? estimatedPnlQuote,
        decimal? estimatedPnlPct,
        decimal minimumProfitPct,
        string exitPolicyDecision,
        string exitPolicyReason,
        bool exitOnReverseSignalOnlyIfProfitable)
    {
        var exitReason = ResolveExitReasonToken(reasonCode, isExitCloseOnly: true) ?? ExitReasonReverseSignal;
        return FormattableString.Invariant(
            $"{ProfitPolicyAppliedToken}; ExitPolicyDecision={exitPolicyDecision}; ExitPolicyReason={exitPolicyReason}; MinTakeProfitPct={minimumProfitPct:0.########}; StopLossPct={optionsValue.StopLossPercentage:0.########}; ExitOnReverseSignalOnlyIfProfitable={exitOnReverseSignalOnlyIfProfitable}; EstimatedPnlQuote={(estimatedPnlQuote.HasValue ? estimatedPnlQuote.Value.ToString("0.########", CultureInfo.InvariantCulture) : "n/a")}; EstimatedPnlPct={(estimatedPnlPct.HasValue ? estimatedPnlPct.Value.ToString("0.########", CultureInfo.InvariantCulture) : "n/a")}; PositionEntryPrice={(entryPrice.HasValue ? entryPrice.Value.ToString("0.########", CultureInfo.InvariantCulture) : "n/a")}; CurrentPrice={(exitPrice.HasValue ? exitPrice.Value.ToString("0.########", CultureInfo.InvariantCulture) : "n/a")}; SignalType=Reverse; ExecutionIntent={ExitCloseOnlyIntentCode}; ExitIntent={ExitCloseOnlyIntentCode}; ExitSource={ExitReasonReverseSignal}; ReverseEntryConvertedToCloseOnly=True; CloseSide={closeSide}; ReduceOnly=True; ExitReason={exitReason}; ExitPnlGuard={exitPnlGuardState}; ReasonCode={reasonCode}");
    }

    private string BuildAutoPositionManagementDisabledReverseSummary(
        string symbol,
        ExecutionOrderSide closeSide,
        decimal openPositionQuantity,
        string? positionAdoptionSummary)
    {
        var summary = FormattableString.Invariant(
            $"SignalType=Reverse; ExecutionIntent={ExitCloseOnlyIntentCode}; ExitIntent={ExitCloseOnlyIntentCode}; EntrySource=StrategyEntry; ExitSource={ExitReasonReverseSignal}; ReverseEntryConvertedToCloseOnly=True; ManualClose=False; PositionAdoption=Adopted; AutoManagementEnabled=False; AutoPositionManagement=False; AutoManagementReason=ConfigDisabled; AdoptedPosition=True; OrphanPositionDetected=False; AmbiguousPositionDetected=False; OpenPositionQuantity={openPositionQuantity:0.########}; CloseSide={closeSide}; ReduceOnly=True; AutoReverse=False; ReasonCode={AutoPositionManagementDisabledDecisionCode}; Detail=AdoptedPositionAutoManagementDisabledFor{symbol}");

        return string.IsNullOrWhiteSpace(positionAdoptionSummary)
            ? summary
            : $"{summary}; {positionAdoptionSummary}";
    }

    private string BuildRuntimeExitQualitySummary(
        string symbol,
        string reasonCode,
        StrategyTradeDirection direction,
        decimal entryPrice,
        decimal exitPrice,
        decimal thresholdPrice,
        decimal netQuantity,
        decimal? peakPrice,
        decimal? breakEvenPrice = null,
        string? trailingStatusSummary = null)
    {
        var closeSide = direction == StrategyTradeDirection.Long ? ExecutionOrderSide.Sell : ExecutionOrderSide.Buy;
        var estimatedPnlQuote = direction == StrategyTradeDirection.Long
            ? NormalizeDecimal((exitPrice - entryPrice) * Math.Abs(netQuantity))
            : NormalizeDecimal((entryPrice - exitPrice) * Math.Abs(netQuantity));
        var estimatedPnlPct = direction == StrategyTradeDirection.Long
            ? NormalizeDecimal(((exitPrice - entryPrice) / entryPrice) * 100m)
            : NormalizeDecimal(((entryPrice - exitPrice) / entryPrice) * 100m);
        var exitReason = ResolveExitReasonToken(reasonCode) ?? ExitReasonRiskExit;
        var peakSummary = peakPrice.HasValue
            ? $"; PeakReferencePrice={peakPrice.Value.ToString("0.########", CultureInfo.InvariantCulture)}"
            : string.Empty;
        var breakEvenSummary = breakEvenPrice.HasValue
            ? $"; BreakEvenPrice={breakEvenPrice.Value.ToString("0.########", CultureInfo.InvariantCulture)}"
            : string.Empty;
        var exitPolicyReason = ResolveRuntimeExitPolicyReason(reasonCode);

        var trailingSummary = string.IsNullOrWhiteSpace(trailingStatusSummary)
            ? string.Empty
            : $"; {trailingStatusSummary}";

        return FormattableString.Invariant(
            $"Runtime exit quality triggered {exitReason} for {symbol}. SignalType=Exit; ExitSource={exitReason}; {ProfitPolicyAppliedToken}; ExitPolicyDecision=Allowed; ExitPolicyReason={exitPolicyReason}{trailingSummary}; CloseSide={closeSide}; ReduceOnly=True; AutoReverse=False; ExecutionIntent=n/a; ExitIntent=n/a; EntrySource=n/a; ReverseEntryConvertedToCloseOnly=False; ManualClose=False; ExitReason={exitReason}; ReasonCode={reasonCode}; MinTakeProfitPct={ResolveReverseExitMinimumProfitPct():0.########}; StopLossPct={optionsValue.StopLossPercentage:0.########}; ExitOnReverseSignalOnlyIfProfitable={optionsValue.ExitOnReverseSignalOnlyIfProfitable}; PositionDirection={direction}; EntryPrice={entryPrice:0.########}; PositionEntryPrice={entryPrice:0.########}; ExitPrice={exitPrice:0.########}; CurrentPrice={exitPrice:0.########}; ThresholdPrice={thresholdPrice:0.########}; EstimatedPnlQuote={estimatedPnlQuote:0.########}; EstimatedPnlPct={estimatedPnlPct:0.########}; NetQuantity={netQuantity:0.########}{peakSummary}{breakEvenSummary}.");
    }

    private string BuildTrailingTakeProfitStatusSummary(
        StrategyTradeDirection direction,
        string trailingState,
        string trailingReason,
        decimal entryPrice,
        decimal? currentPrice,
        decimal? trailingActivationPrice,
        decimal? trailingReferencePrice,
        decimal? trailingStopPrice)
    {
        var referenceType = direction == StrategyTradeDirection.Long ? "Peak" : "Trough";

        return FormattableString.Invariant(
            $"TrailingEnabled={optionsValue.EnableTrailingTakeProfit}; TrailingState={trailingState}; TrailingReason={trailingReason}; TrailingActivationPct={optionsValue.TrailingStopActivationPercentage:0.########}; TrailingDistancePct={optionsValue.TrailingStopPercentage:0.########}; TrailingActivationPrice={(trailingActivationPrice.HasValue ? trailingActivationPrice.Value.ToString("0.########", CultureInfo.InvariantCulture) : "n/a")}; TrailingReferenceType={referenceType}; TrailingReferencePrice={(trailingReferencePrice.HasValue ? trailingReferencePrice.Value.ToString("0.########", CultureInfo.InvariantCulture) : "n/a")}; TrailingStopPrice={(trailingStopPrice.HasValue ? trailingStopPrice.Value.ToString("0.########", CultureInfo.InvariantCulture) : "n/a")}; TrailingFailClosed={(trailingState == "Blocked" ? "True" : "False")}");
    }

    private decimal ResolveReverseExitMinimumProfitPct()
    {
        return optionsValue.MinTakeProfitPct < 0m
            ? DefaultExitCloseOnlyMinimumProfitPct
            : optionsValue.MinTakeProfitPct;
    }

    private static string ResolveRuntimeExitPolicyReason(string reasonCode)
    {
        return reasonCode switch
        {
            TakeProfitTriggeredDecisionCode => TakeProfitThresholdReachedPolicyReason,
            StopLossTriggeredDecisionCode => StopLossThresholdReachedPolicyReason,
            TrailingStopTriggeredDecisionCode => TrailingTakeProfitRetracePolicyReason,
            BreakEvenTriggeredDecisionCode => RiskExitThresholdReachedPolicyReason,
            _ => reasonCode
        };
    }

    private static bool IsExitCloseOnlyDecision(string reasonCode, string summary)
    {
        return reasonCode.StartsWith("ExitCloseOnly", StringComparison.Ordinal) ||
               summary.Contains($"ExecutionIntent={ExitCloseOnlyIntentCode}", StringComparison.Ordinal) ||
               summary.Contains("ExitPnlGuard=", StringComparison.Ordinal);
    }

    private static string AppendSignalClassificationDecisionSummary(
        string summary,
        StrategySignalType signalType,
        string reasonCode,
        bool isExitCloseOnly)
    {
        if (summary.Contains("ReverseEntryConvertedToCloseOnly=", StringComparison.Ordinal))
        {
            return summary;
        }

        string classification = isExitCloseOnly
            ? $"SignalType=Reverse; ExecutionIntent={ExitCloseOnlyIntentCode}; ExitIntent={ExitCloseOnlyIntentCode}; EntrySource=StrategyEntry; ExitSource={ExitReasonReverseSignal}; ReverseEntryConvertedToCloseOnly=True; ManualClose=False;"
            : signalType == StrategySignalType.Entry
                ? "SignalType=Entry; ExecutionIntent=Entry; ExitIntent=n/a; EntrySource=StrategyEntry; ExitSource=n/a; ReverseEntryConvertedToCloseOnly=False; ManualClose=False;"
                : $"SignalType=Exit; ExecutionIntent=n/a; ExitIntent=n/a; EntrySource=n/a; ExitSource={ResolveExitReasonToken(reasonCode) ?? "StrategyExit"}; ReverseEntryConvertedToCloseOnly=False; ManualClose=False;";

        return string.IsNullOrWhiteSpace(summary)
            ? classification
            : $"{classification} {summary}";
    }

    private static string AppendExitReasonToken(string summary, string reasonCode, bool isExitCloseOnly)
    {
        var exitReason = ResolveExitReasonToken(reasonCode, isExitCloseOnly);
        if (string.IsNullOrWhiteSpace(exitReason) ||
            summary.Contains("ExitReason=", StringComparison.Ordinal))
        {
            return summary;
        }

        return $"{summary} ExitReason={exitReason};";
    }

    private static string? ResolveExitReasonToken(string reasonCode, bool isExitCloseOnly = false)
    {
        return reasonCode switch
        {
            TakeProfitTriggeredDecisionCode => ExitReasonTakeProfit,
            TrailingStopTriggeredDecisionCode => ExitReasonTrailingTakeProfit,
            StopLossTriggeredDecisionCode => ExitReasonStopLoss,
            BreakEvenTriggeredDecisionCode or ExitCloseOnlyBlockedRiskDecisionCode => ExitReasonRiskExit,
            ExitCloseOnlyBlockedUnprofitableLongDecisionCode or ExitCloseOnlyBlockedUnprofitableShortDecisionCode => ExitReasonBlockedUnprofitable,
            ExitCloseOnlyBlockedPrivatePlaneStaleDecisionCode or "PrivatePlaneStale" => ExitReasonPrivatePlaneStale,
            "StaleMarketData" => ExitReasonStaleMarketData,
            _ when reasonCode.Contains("Manual", StringComparison.Ordinal) => ExitReasonManual,
            _ when reasonCode.Contains("Emergency", StringComparison.Ordinal) => ExitReasonEmergency,
            _ when isExitCloseOnly => ExitReasonReverseSignal,
            _ => null
        };
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

    private static decimal NormalizeDecimal(decimal value)
    {
        return decimal.Round(value, 8, MidpointRounding.AwayFromZero);
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private sealed record CurrentPositionSnapshot(
        StrategyTradeDirection Direction,
        decimal NetQuantity,
        decimal EntryPrice,
        decimal BreakEvenPrice,
        decimal UnrealizedProfit,
        DateTime EntryOpenedAtUtc,
        Guid? LastFilledEntryOrderId,
        DateTime? LastFilledEntryAtUtc);

    private sealed record RuntimeExitQualityTrigger(
        StrategyTradeDirection Direction,
        string DecisionReasonCode,
        string DecisionSummary,
        decimal ThresholdPrice,
        decimal ReferencePrice,
        decimal? PeakPrice);

    private sealed record RuntimeExitQualityEvaluation(
        RuntimeExitQualityTrigger? Trigger,
        string? TrailingStatusSummary);

    private sealed record OrderExecutionBreakerSnapshot(
        CircuitBreakerStateCode StateCode,
        DateTime? CooldownUntilUtc,
        string? LastErrorCode);

    private sealed record CloseOnlyExecutionIntent(
        StrategyTradeDirection CurrentPositionDirection,
        decimal OpenPositionQuantity,
        ExecutionOrderSide CloseSide,
        string? ExitPnlGuardSummary,
        string? PositionAdoptionSummary);

    private sealed record CloseOnlyPnlGuardEvaluation(
        bool IsBlocked,
        string DecisionReasonCode,
        string DecisionSummary,
        string GuardSummary,
        decimal? CloseQuantity);

    private sealed record PilotDispatchPlan(
        ExecutionOrderSide Side,
        decimal Quantity,
        bool ReduceOnly);

    private sealed record PilotLeveragePolicyEvaluation(
        bool IsBlocked,
        decimal RequestedLeverage,
        decimal EffectiveLeverage,
        decimal MaxAllowedLeverage,
        string LeverageSource,
        string Decision,
        string ReasonCode,
        bool AlignmentSkippedForReduceOnly,
        string Summary);

    private sealed record SymbolExecutionAllowlistEvaluation(
        bool IsConfigured,
        bool IsBlocked,
        string Decision,
        string ReasonCode,
        string? Summary)
    {
        public static SymbolExecutionAllowlistEvaluation NotConfigured { get; } =
            new(false, false, "Allowed", SymbolAllowlistAllowedReasonCode, null);
    }

    private sealed record PositionAdoptionResolution(
        string State,
        string Summary,
        bool IsAmbiguous)
    {
        public static PositionAdoptionResolution Skipped { get; } = new(
            "Skipped",
            "PositionAdoption=Skipped; AdoptionReason=NoOpenPosition; AutoManagementEnabled=False; AutoPositionManagement=False; AutoManagementReason=NoOpenPosition; AdoptedPosition=False; OrphanPositionDetected=False; AmbiguousPositionDetected=False",
            false);

        public static PositionAdoptionResolution Adopted(
            string symbol,
            decimal netQuantity,
            Guid exchangeAccountId,
            Guid botId,
            bool autoManagementEnabled)
        {
            return new PositionAdoptionResolution(
                PositionAdoptionAdoptedState,
                BuildPositionAdoptionSummary(
                    PositionAdoptionAdoptedState,
                    symbol,
                    netQuantity,
                    exchangeAccountId,
                    botId,
                    "ExactBotAccountSymbolMatch",
                    autoManagementEnabled,
                    autoManagementEnabled ? "ConfigEnabled" : "ConfigDisabled"),
                false);
        }

        public static PositionAdoptionResolution Ambiguous(
            string symbol,
            decimal netQuantity,
            Guid exchangeAccountId)
        {
            return new PositionAdoptionResolution(
                PositionAdoptionAmbiguousState,
                BuildPositionAdoptionSummary(
                    PositionAdoptionAmbiguousState,
                    symbol,
                    netQuantity,
                    exchangeAccountId,
                    null,
                    "MultipleBotsMatchExactPositionScope",
                    false,
                    "AmbiguousPositionScope"),
                true);
        }
    }

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

    private static string BuildDispatchIdempotencyKey(
        StrategySignalSnapshot signal,
        ExecutionEnvironment executionDispatchMode,
        PilotDispatchPlan dispatchPlan)
    {
        return $"scanner-handoff:{signal.StrategySignalId:N}:{executionDispatchMode}:{signal.SignalType}:{dispatchPlan.Side}:{dispatchPlan.ReduceOnly}";
    }
}
