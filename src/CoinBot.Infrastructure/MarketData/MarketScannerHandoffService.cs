
using System.Globalization;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using CoinBot.Application.Abstractions.Administration;
using CoinBot.Application.Abstractions.DataScope;
using CoinBot.Application.Abstractions.DemoPortfolio;
using CoinBot.Application.Abstractions.Execution;
using CoinBot.Application.Abstractions.Indicators;
using CoinBot.Application.Abstractions.MarketData;
using CoinBot.Application.Abstractions.Risk;
using CoinBot.Application.Abstractions.Strategies;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Execution;
using CoinBot.Infrastructure.Jobs;
using CoinBot.Infrastructure.Observability;
using CoinBot.Infrastructure.Persistence;
using CoinBot.Infrastructure.Strategies;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CoinBot.Infrastructure.MarketData;

public sealed class MarketScannerHandoffService(
    ApplicationDbContext dbContext,
    IServiceScopeFactory serviceScopeFactory,
    IMarketDataService marketDataService,
    IIndicatorDataService indicatorDataService,
    ISharedSymbolRegistry sharedSymbolRegistry,
    IDataLatencyCircuitBreaker dataLatencyCircuitBreaker,
    IOptions<MarketScannerOptions> scannerOptions,
    IOptions<BinanceMarketDataOptions> marketDataOptions,
    IOptions<BotExecutionPilotOptions> botExecutionPilotOptions,
    TimeProvider timeProvider,
    ILogger<MarketScannerHandoffService> logger,
    IBinanceExchangeInfoClient? exchangeInfoClient = null,
    IHostEnvironment? hostEnvironment = null,
    ITradingModeResolver? tradingModeResolver = null,
    IOptions<ExecutionRuntimeOptions>? executionRuntimeOptions = null,
    IUltraDebugLogService? ultraDebugLogService = null,
    ITraceService? traceService = null,
    ICorrelationContextAccessor? correlationContextAccessor = null)
{
    private static readonly JsonSerializerOptions StrategySignalSerializerOptions = CreateStrategySignalSerializerOptions();
    private static readonly HashSet<string> ReplaySuppressedBlockedAttemptCodes = new(StringComparer.Ordinal)
    {
        "DuplicateExecutionRequestSuppressed",
        "LongEntryHysteresisActive",
        "ShortEntryHysteresisActive",
        "UserExecutionBotCooldownActive",
        "UserExecutionSymbolCooldownActive",
        "UserExecutionMaxOpenPositionsExceeded",
        "SameDirectionLongEntrySuppressed",
        "SameDirectionShortEntrySuppressed",
        "ReverseBlockedOpenPositionExists"
    };

    private readonly MarketScannerOptions scannerOptionsValue = scannerOptions.Value;
    private readonly BotExecutionPilotOptions botExecutionOptionsValue = botExecutionPilotOptions.Value;
    private readonly string klineInterval = string.IsNullOrWhiteSpace(marketDataOptions.Value.KlineInterval)
        ? "1m"
        : marketDataOptions.Value.KlineInterval.Trim();
    private readonly string[] allowedQuoteAssets = (scannerOptions.Value.AllowedQuoteAssets ?? [])
        .Select(item => item?.Trim().ToUpperInvariant())
        .Where(item => !string.IsNullOrWhiteSpace(item))
        .Distinct(StringComparer.Ordinal)
        .OrderByDescending(item => item.Length)
        .ThenBy(item => item, StringComparer.Ordinal)
        .ToArray();
    private readonly ExecutionRuntimeOptions executionRuntimeOptionsValue = executionRuntimeOptions?.Value ?? new ExecutionRuntimeOptions();

    public async Task<MarketScannerHandoffAttempt> RunOnceAsync(Guid scanCycleId, CancellationToken cancellationToken = default)
    {
        if (!scannerOptionsValue.HandoffEnabled)
        {
            return await PersistBlockedAttemptAsync(
                scanCycleId,
                selectedCandidate: null,
                ownerUserId: null,
                botMatch: null,
                symbolMetadata: null,
                executionContext: null,
                strategySignal: null,
                strategyVeto: null,
                strategyDecisionOutcome: "NotEvaluated",
                executionStatus: "Disabled",
                blockerCode: "ScannerHandoffDisabled",
                blockerDetail: "Scanner handoff is disabled by configuration.",
                guardSummary: "HandoffEnabled=False; Source=MarketData:Scanner:HandoffEnabled.",
                cancellationToken);
        }

        var cycleCandidates = await dbContext.MarketScannerCandidates
            .AsNoTracking()
            .Where(entity => entity.ScanCycleId == scanCycleId && entity.IsEligible && !entity.IsDeleted)
            .OrderBy(entity => entity.Rank ?? int.MaxValue)
            .ThenByDescending(entity => entity.Score)
            .ThenBy(entity => entity.Symbol)
            .ToListAsync(cancellationToken);

        var candidates = cycleCandidates
            .Where(entity => !MarketScannerCandidateIntegrityGuard.HasLegacyDirtyMarketScore(entity))
            .ToList();

        if (candidates.Count == 0)
        {
            await RunDemoConsistencyForScanSymbolsAsync(scanCycleId, cancellationToken);
            return await PersistBlockedAttemptAsync(
                scanCycleId,
                selectedCandidate: null,
                ownerUserId: null,
                botMatch: null,
                symbolMetadata: null,
                executionContext: null,
                strategySignal: null,
                strategyVeto: null,
                strategyDecisionOutcome: "NotEvaluated",
                executionStatus: "Blocked",
                blockerCode: "NoEligibleCandidate",
                blockerDetail: "Scanner handoff did not find an eligible candidate in the latest scan cycle.",
                guardSummary: "CandidateSelection=None",
                cancellationToken);
        }

        MarketScannerHandoffAttempt? latestAttempt = null;

        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var symbol = MarketDataSymbolNormalizer.Normalize(candidate.Symbol);
            var ownerBotMatch = await ResolveBotMatchAsync(symbol, cancellationToken);

            if (ownerBotMatch is null)
            {
                latestAttempt = await PersistBlockedAttemptAsync(
                    scanCycleId,
                    candidate,
                    ownerUserId: null,
                    botMatch: null,
                    symbolMetadata: null,
                    executionContext: null,
                    strategySignal: null,
                    strategyVeto: null,
                    strategyDecisionOutcome: "NotEvaluated",
                    executionStatus: "Blocked",
                    blockerCode: "NoMatchingEnabledBot",
                    blockerDetail: $"No enabled bot with a published strategy was available for {symbol}.",
                    guardSummary: $"CandidateSymbol={symbol}; CandidateRank={candidate.Rank?.ToString(CultureInfo.InvariantCulture) ?? "n/a"}",
                    cancellationToken: cancellationToken);
                continue;
            }

            if (!TryResolvePilotExecutionParameters(ownerBotMatch, out var leverage, out var marginType, out var parameterFailureCode))
            {
                latestAttempt = await PersistBlockedAttemptAsync(
                    scanCycleId,
                    candidate,
                    ownerBotMatch.OwnerUserId,
                    ownerBotMatch,
                    symbolMetadata: null,
                    executionContext: null,
                    strategySignal: null,
                    strategyVeto: null,
                    strategyDecisionOutcome: "NotEvaluated",
                    executionStatus: "Blocked",
                    blockerCode: parameterFailureCode ?? "PilotParametersInvalid",
                    blockerDetail: BuildPilotParameterFailureDetail(parameterFailureCode),
                    guardSummary: $"PilotExecutionParameters={parameterFailureCode ?? "Invalid"}; Symbol={symbol}; BotId={ownerBotMatch.BotId:N}",
                    cancellationToken: cancellationToken);
                continue;
            }

            await marketDataService.TrackSymbolAsync(symbol, cancellationToken);
            await indicatorDataService.TrackSymbolAsync(symbol, cancellationToken);

            var symbolMetadata = await ResolveSymbolMetadataAsync(symbol, cancellationToken);
            if (symbolMetadata is null)
            {
                latestAttempt = await PersistBlockedAttemptAsync(
                    scanCycleId,
                    candidate,
                    ownerBotMatch.OwnerUserId,
                    ownerBotMatch,
                    symbolMetadata: null,
                    executionContext: null,
                    strategySignal: null,
                    strategyVeto: null,
                    strategyDecisionOutcome: "NotEvaluated",
                    executionStatus: "Blocked",
                    blockerCode: "SymbolMetadataUnavailable",
                    blockerDetail: $"Scanner handoff could not resolve symbol metadata for {symbol}.",
                    guardSummary: $"SymbolMetadata=Unavailable; Symbol={symbol}",
                    cancellationToken: cancellationToken);
                continue;
            }
            var marketState = await ResolveMarketStateAsync(symbol, klineInterval, cancellationToken);

            if (marketState.IndicatorSnapshot is null)
            {
                latestAttempt = await PersistBlockedAttemptAsync(
                    scanCycleId,
                    candidate,
                    ownerBotMatch.OwnerUserId,
                    ownerBotMatch,
                    symbolMetadata,
                    executionContext: null,
                    strategySignal: null,
                    strategyVeto: null,
                    strategyDecisionOutcome: "NotEvaluated",
                    executionStatus: "Blocked",
                    blockerCode: "MissingFreshSignalData",
                    blockerDetail: $"Scanner handoff could not resolve a ready indicator snapshot for {symbol} {klineInterval}.",
                    guardSummary: $"IndicatorState=Unavailable; Symbol={symbol}; Timeframe={klineInterval}",
                    cancellationToken: cancellationToken);
                continue;
            }

            if (!marketState.ReferencePrice.HasValue || marketState.ReferencePrice.Value <= 0m)
            {
                latestAttempt = await PersistBlockedAttemptAsync(
                    scanCycleId,
                    candidate,
                    ownerBotMatch.OwnerUserId,
                    ownerBotMatch,
                    symbolMetadata,
                    executionContext: null,
                    strategySignal: null,
                    strategyVeto: null,
                    strategyDecisionOutcome: "NotEvaluated",
                    executionStatus: "Blocked",
                    blockerCode: "ReferencePriceUnavailable",
                    blockerDetail: $"Scanner handoff could not resolve a reference price for {symbol}.",
                    guardSummary: $"ReferencePrice=Unavailable; Symbol={symbol}",
                    cancellationToken: cancellationToken);
                continue;
            }
            using var userScope = serviceScopeFactory.CreateScope();
            var dataScopeAccessor = userScope.ServiceProvider.GetRequiredService<IDataScopeContextAccessor>();
            using var scopeOverride = dataScopeAccessor.BeginScope(ownerBotMatch.OwnerUserId);
            var strategySignalService = userScope.ServiceProvider.GetRequiredService<IStrategySignalService>();
            var executionGate = userScope.ServiceProvider.GetRequiredService<IExecutionGate>();
            var userExecutionOverrideGuard = userScope.ServiceProvider.GetRequiredService<IUserExecutionOverrideGuard>();
            var resolvedTradingModeResolver = tradingModeResolver ?? userScope.ServiceProvider.GetService<ITradingModeResolver>();
            var executionEnvironment = botExecutionOptionsValue.PilotActivationEnabled
                ? botExecutionOptionsValue.ExecutionDispatchMode
                : resolvedTradingModeResolver is null
                    ? botExecutionOptionsValue.ExecutionDispatchMode
                    : await ResolveHandoffExecutionEnvironmentAsync(
                        resolvedTradingModeResolver,
                        ownerBotMatch,
                        cancellationToken: cancellationToken);
            var strategyResult = await strategySignalService.GenerateAsync(
                new GenerateStrategySignalsRequest(
                    ownerBotMatch.TradingStrategyVersionId,
                    new StrategyEvaluationContext(botExecutionOptionsValue.SignalEvaluationMode, marketState.IndicatorSnapshot),
                    EffectiveExecutionEnvironment: executionEnvironment),
                cancellationToken);

            var strategySignal = SelectActionableEntrySignal(strategyResult, symbol, klineInterval);
            var strategyVeto = SelectVeto(strategyResult, symbol, klineInterval);
            var duplicateSignalResolution = DuplicateSignalResolution.None;

            if (strategySignal is null && strategyResult.SuppressedDuplicateCount > 0)
            {
                duplicateSignalResolution = await ResolveDuplicateEntrySignalAsync(
                    ownerBotMatch,
                    marketState.IndicatorSnapshot,
                    executionEnvironment,
                    cancellationToken: cancellationToken);
                strategySignal = duplicateSignalResolution.Signal;
            }

            if (strategySignal is null)
            {
                var strategyBlocker = ResolveStrategyBlocker(strategyResult, strategyVeto, symbol, klineInterval);
                latestAttempt = await PersistBlockedAttemptAsync(
                    scanCycleId,
                    candidate,
                    ownerBotMatch.OwnerUserId,
                    ownerBotMatch,
                    symbolMetadata,
                    executionContext: null,
                    strategySignal: null,
                    strategyVeto,
                    strategyBlocker.StrategyOutcome,
                    executionStatus: "Blocked",
                    strategyBlocker.BlockerCode,
                    strategyBlocker.BlockerDetail,
                    strategyBlocker.GuardSummary,
                    cancellationToken);
                continue;
            }

            var attemptCorrelationId = await ResolveAttemptCorrelationIdAsync(strategySignal, cancellationToken);

            PreparedExecutionContext executionContext;
            var entryDirection = ResolveSignalDirection(strategySignal);
            try
            {
                executionContext = new PreparedExecutionContext(
                    Side: ResolveEntrySide(entryDirection),
                    OrderType: ExecutionOrderType.Market,
                    Environment: executionEnvironment,
                    Quantity: ResolveHandoffQuantity(symbolMetadata, marketState.ReferencePrice.Value),
                    Price: marketState.ReferencePrice.Value);
            }
            catch (ExecutionValidationException exception)
            {
                latestAttempt = await PersistBlockedAttemptAsync(
                    scanCycleId,
                    candidate,
                    ownerBotMatch.OwnerUserId,
                    ownerBotMatch,
                    symbolMetadata,
                    executionContext: null,
                    strategySignal,
                    strategyVeto: null,
                    strategyDecisionOutcome: "Persisted",
                    executionStatus: "Blocked",
                    blockerCode: exception.ReasonCode,
                    blockerDetail: exception.Message,
                    correlationId: attemptCorrelationId,
                    guardSummary: Truncate(
                        $"StrategySignalDirection={entryDirection}; Symbol={symbol}; Timeframe={klineInterval}",
                        512) ?? $"StrategySignalDirection={entryDirection}; Symbol={symbol}; Timeframe={klineInterval}",
                    cancellationToken: cancellationToken);
                continue;
            }

            if (TryResolveEntryDirectionModeBlock(ownerBotMatch.DirectionMode, symbol, entryDirection, out var directionModeBlockSummary))
            {
                latestAttempt = await PersistBlockedAttemptAsync(
                    scanCycleId,
                    candidate,
                    ownerBotMatch.OwnerUserId,
                    ownerBotMatch,
                    symbolMetadata,
                    executionContext,
                    strategySignal,
                    strategyVeto: null,
                    strategyDecisionOutcome: "Persisted",
                    executionStatus: "Blocked",
                    blockerCode: "EntryDirectionModeBlocked",
                    blockerDetail: directionModeBlockSummary ?? "Execution blocked because bot direction mode does not allow the requested entry direction.",
                    correlationId: attemptCorrelationId,
                    guardSummary: Truncate(
                        $"EntryDirectionModeBlocked; BotDirectionMode={ownerBotMatch.DirectionMode}; EntryDirection={entryDirection}; Symbol={symbol}; Timeframe={klineInterval}",
                        512) ?? $"EntryDirectionModeBlocked; BotDirectionMode={ownerBotMatch.DirectionMode}; EntryDirection={entryDirection}; Symbol={symbol}; Timeframe={klineInterval}",
                    cancellationToken: cancellationToken);
                continue;
            }

            var replayPreparedAttempt = await ResolveReplaySuppressedPreparedAttemptAsync(
                ownerBotMatch.OwnerUserId,
                strategySignal,
                executionContext,
                cancellationToken);
            if (replayPreparedAttempt is not null)
            {
                logger.LogInformation(
                    "Market scanner handoff skipped duplicate prepared replay. ExistingHandoffAttemptId={HandoffAttemptId} StrategySignalId={StrategySignalId} ExecutionEnvironment={ExecutionEnvironment} ExecutionSide={ExecutionSide}.",
                    replayPreparedAttempt.Id,
                    replayPreparedAttempt.StrategySignalId,
                    replayPreparedAttempt.ExecutionEnvironment,
                    replayPreparedAttempt.ExecutionSide);
                return replayPreparedAttempt;
            }

            var hasExistingExecutionIntent = duplicateSignalResolution.HasExistingExecutionRequest ||
                await HasExistingExecutionOrderIntentAsync(
                    ownerBotMatch.OwnerUserId,
                    strategySignal,
                    executionContext,
                    cancellationToken);

            if (hasExistingExecutionIntent)
            {
                latestAttempt = await PersistBlockedAttemptAsync(
                    scanCycleId,
                    candidate,
                    ownerBotMatch.OwnerUserId,
                    ownerBotMatch,
                    symbolMetadata,
                    executionContext,
                    strategySignal,
                    strategyVeto: null,
                    strategyDecisionOutcome: "Persisted",
                    executionStatus: "Blocked",
                    blockerCode: "DuplicateExecutionRequestSuppressed",
                    blockerDetail: "Scanner handoff skipped execution request creation because this strategy signal already has a prepared handoff or execution order.",
                    correlationId: attemptCorrelationId,
                    guardSummary: Truncate(
                        $"DuplicateExecutionRequest=Suppressed; StrategySignalId={strategySignal.StrategySignalId:N}; IndicatorCloseTimeUtc={strategySignal.IndicatorCloseTimeUtc:O}; Symbol={symbol}; Timeframe={klineInterval}",
                        512)
                        ?? $"DuplicateExecutionRequest=Suppressed; Symbol={symbol}; Timeframe={klineInterval}",
                    cancellationToken: cancellationToken);
                continue;
            }

            if (IsActionableDirection(entryDirection))
            {
                var hysteresisSummary = await ResolveEntryHysteresisSummaryAsync(
                    ownerBotMatch,
                    symbol,
                    entryDirection,
                    marketState.ReferencePrice,
                    cancellationToken);

                if (!string.IsNullOrWhiteSpace(hysteresisSummary))
                {
                    latestAttempt = await PersistBlockedAttemptAsync(
                        scanCycleId,
                        candidate,
                        ownerBotMatch.OwnerUserId,
                        ownerBotMatch,
                        symbolMetadata,
                        executionContext,
                        strategySignal,
                        strategyVeto: null,
                        strategyDecisionOutcome: "Persisted",
                        executionStatus: "Blocked",
                        blockerCode: ResolveEntryHysteresisActiveBlockerCode(entryDirection),
                        blockerDetail: hysteresisSummary,
                        correlationId: attemptCorrelationId,
                        guardSummary: Truncate(
                            $"EntryHysteresis=Active; EntryDirection={entryDirection}; Symbol={symbol}; Timeframe={klineInterval}",
                            512) ?? $"EntryHysteresis=Active; Symbol={symbol}; Timeframe={klineInterval}",
                        cancellationToken: cancellationToken);
                    continue;
                }

                var currentNetQuantity = await ResolveCurrentNetQuantityAsync(
                    ownerBotMatch,
                    symbol,
                    executionContext.Environment,
                    cancellationToken: cancellationToken);

                if (currentNetQuantity != 0m)
                {
                    var currentPositionDirection = currentNetQuantity > 0m
                        ? StrategyTradeDirection.Long
                        : StrategyTradeDirection.Short;

                    if (currentPositionDirection == entryDirection)
                    {
                        latestAttempt = await PersistBlockedAttemptAsync(
                            scanCycleId,
                            candidate,
                            ownerBotMatch.OwnerUserId,
                            ownerBotMatch,
                            symbolMetadata,
                            executionContext,
                            strategySignal,
                            strategyVeto: null,
                            strategyDecisionOutcome: "Persisted",
                            executionStatus: "Blocked",
                            blockerCode: ResolveSameDirectionEntrySuppressedBlockerCode(entryDirection),
                            blockerDetail: $"Entry signal was suppressed because an open {entryDirection.ToString().ToLowerInvariant()} position already exists for {symbol} on the selected exchange account.",
                            correlationId: attemptCorrelationId,
                            guardSummary: Truncate(
                                $"OpenPositionSuppression=SameDirection; EntryDirection={entryDirection}; CurrentNetQuantity={currentNetQuantity:0.########}; Symbol={symbol}; Timeframe={klineInterval}",
                                512) ?? $"OpenPositionSuppression=SameDirection; Symbol={symbol}; Timeframe={klineInterval}",
                            cancellationToken: cancellationToken);
                        continue;
                    }

                    latestAttempt = await PersistBlockedAttemptAsync(
                        scanCycleId,
                        candidate,
                        ownerBotMatch.OwnerUserId,
                        ownerBotMatch,
                        symbolMetadata,
                        executionContext,
                        strategySignal,
                        strategyVeto: null,
                        strategyDecisionOutcome: "Persisted",
                        executionStatus: "Blocked",
                        blockerCode: "ReverseBlockedOpenPositionExists",
                        blockerDetail: $"Entry signal was suppressed because reverse entries are disabled. CurrentPositionDirection={currentPositionDirection}; RequestedEntryDirection={entryDirection}; Symbol={symbol}.",
                        correlationId: attemptCorrelationId,
                        guardSummary: Truncate(
                            $"OpenPositionSuppression=ReverseBlocked; EntryDirection={entryDirection}; CurrentPositionDirection={currentPositionDirection}; CurrentNetQuantity={currentNetQuantity:0.########}; Symbol={symbol}; Timeframe={klineInterval}",
                            512) ?? $"OpenPositionSuppression=ReverseBlocked; Symbol={symbol}; Timeframe={klineInterval}",
                        cancellationToken: cancellationToken);
                    continue;
                }
            }

            try
            {
                var gateContext = BuildGateContext(
                    scanCycleId,
                    candidate,
                    ownerBotMatch,
                    executionContext.Environment,
                    botExecutionOptionsValue,
                    marginType,
                    leverage);
                var correlationId = attemptCorrelationId;
                var executionExchangeAccountId = ownerBotMatch.ExchangeAccountId;

                await executionGate.EnsureExecutionAllowedAsync(
                    new ExecutionGateRequest(
                        Actor: "system:market-scanner",
                        Action: "MarketScanner.Handoff",
                        Target: $"MarketScannerCandidate/{candidate.Id:N}",
                        Environment: executionContext.Environment,
                        Context: gateContext,
                        CorrelationId: correlationId,
                        UserId: ownerBotMatch.OwnerUserId,
                        BotId: ownerBotMatch.BotId,
                        StrategyKey: ownerBotMatch.StrategyKey,
                        Symbol: symbol,
                        Timeframe: klineInterval,
                        ExchangeAccountId: executionExchangeAccountId,
                        Plane: ExchangeDataPlane.Futures),
                    cancellationToken: cancellationToken);
                var latencySnapshot = await dataLatencyCircuitBreaker.GetSnapshotAsync(
                    correlationId,
                    symbol,
                    klineInterval,
                    cancellationToken: cancellationToken);

                var overrideEvaluation = await userExecutionOverrideGuard.EvaluateAsync(
                    new UserExecutionOverrideEvaluationRequest(
                        ownerBotMatch.OwnerUserId,
                        symbol,
                        executionContext.Environment,
                        executionContext.Side,
                        executionContext.Quantity,
                        executionContext.Price,
                        ownerBotMatch.BotId,
                        ownerBotMatch.StrategyKey,
                        gateContext,
                        ownerBotMatch.TradingStrategyId,
                        ownerBotMatch.TradingStrategyVersionId,
                        klineInterval),
                    cancellationToken: cancellationToken);

                if (overrideEvaluation.IsBlocked)
                {
                    latestAttempt = await PersistBlockedAttemptAsync(
                        scanCycleId,
                        candidate,
                        ownerBotMatch.OwnerUserId,
                        ownerBotMatch,
                        symbolMetadata,
                        executionContext,
                        strategySignal,
                        strategyVeto: null,
                    strategyDecisionOutcome: "Persisted",
                    executionStatus: "Blocked",
                    blockerCode: overrideEvaluation.BlockCode ?? "UserExecutionOverrideBlocked",
                    blockerDetail: overrideEvaluation.Message ?? "Scanner handoff was blocked by user execution override guard.",
                    correlationId: correlationId,
                    guardSummary: Truncate(
                        $"UserExecutionOverrideGuard={overrideEvaluation.BlockCode ?? "Blocked"}; Symbol={symbol}; Timeframe={klineInterval}; {BuildLatencyGuardSummarySnippet(latencySnapshot)}; RiskSummary={overrideEvaluation.RiskEvaluation?.ReasonSummary ?? "n/a"}",
                        512) ?? $"UserExecutionOverrideGuard={overrideEvaluation.BlockCode ?? "Blocked"}; Symbol={symbol}; Timeframe={klineInterval}",
                    cancellationToken: cancellationToken,
                    riskEvaluation: overrideEvaluation.RiskEvaluation);
                continue;
            }

                var executionEngine = userScope.ServiceProvider.GetRequiredService<IExecutionEngine>();
                var dispatchResult = await executionEngine.DispatchAsync(
                    new ExecutionCommand(
                        Actor: "system:market-scanner",
                        OwnerUserId: ownerBotMatch.OwnerUserId,
                        TradingStrategyId: strategySignal.TradingStrategyId,
                        TradingStrategyVersionId: strategySignal.TradingStrategyVersionId,
                        StrategySignalId: strategySignal.StrategySignalId,
                        SignalType: strategySignal.SignalType,
                        StrategyKey: ownerBotMatch.StrategyKey,
                        Symbol: symbol,
                        Timeframe: klineInterval,
                        BaseAsset: ResolveBaseAsset(symbol, symbolMetadata),
                        QuoteAsset: ResolveQuoteAsset(symbol, symbolMetadata),
                        Side: executionContext.Side,
                        OrderType: executionContext.OrderType,
                        Quantity: executionContext.Quantity,
                        Price: executionContext.Price,
                        BotId: ownerBotMatch.BotId,
                        ExchangeAccountId: executionExchangeAccountId,
                        IsDemo: executionContext.Environment == ExecutionEnvironment.Demo,
                        IdempotencyKey: BuildScannerHandoffIdempotencyKey(strategySignal, executionContext),
                        CorrelationId: correlationId,
                        ParentCorrelationId: null,
                        Context: Truncate(
                            $"{gateContext}; MarketScannerHandoff=Prepared; ScanCycleId={scanCycleId:N}; CandidateId={candidate.Id:N}",
                            1024),
                        Plane: ExchangeDataPlane.Futures),
                    cancellationToken);

                latestAttempt = await PersistPreparedAttemptAsync(
                    scanCycleId,
                    candidate,
                    ownerBotMatch.OwnerUserId,
                    ownerBotMatch,
                    symbolMetadata,
                    executionContext,
                    latencySnapshot,
                    strategySignal,
                    strategyResult,
                    dispatchResult,
                    correlationId,
                    cancellationToken);
                return latestAttempt;
            }
            catch (ExecutionGateRejectedException exception)
            {
                latestAttempt = await PersistBlockedAttemptAsync(
                    scanCycleId,
                    candidate,
                    ownerBotMatch.OwnerUserId,
                    ownerBotMatch,
                    symbolMetadata,
                    executionContext,
                    strategySignal,
                    strategyVeto: null,
                    strategyDecisionOutcome: "Persisted",
                    executionStatus: "Blocked",
                    blockerCode: exception.Reason.ToString(),
                    blockerDetail: exception.Message,
                    correlationId: attemptCorrelationId,
                    guardSummary: $"ExecutionGate={exception.Reason}; Symbol={symbol}; Timeframe={klineInterval}",
                    cancellationToken: cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                latestAttempt = await PersistBlockedAttemptAsync(
                    scanCycleId,
                    candidate,
                    ownerBotMatch.OwnerUserId,
                    ownerBotMatch,
                    symbolMetadata,
                    executionContext,
                    strategySignal,
                    strategyVeto: null,
                    strategyDecisionOutcome: "Persisted",
                    executionStatus: "Blocked",
                    blockerCode: exception.GetType().Name,
                    blockerDetail: exception.Message,
                    correlationId: attemptCorrelationId,
                    guardSummary: $"ScannerHandoffException={exception.GetType().Name}; Symbol={symbol}; Timeframe={klineInterval}",
                    cancellationToken: cancellationToken);
            }
        }

        return latestAttempt ?? await PersistBlockedAttemptAsync(
            scanCycleId,
            selectedCandidate: null,
            ownerUserId: null,
            botMatch: null,
            symbolMetadata: null,
            executionContext: null,
            strategySignal: null,
            strategyVeto: null,
            strategyDecisionOutcome: "NotEvaluated",
            executionStatus: "Blocked",
            blockerCode: "NoEligibleCandidatePrepared",
            blockerDetail: "Scanner handoff evaluated the scan cycle but no candidate produced an executable request.",
            guardSummary: "CandidateSelection=Exhausted",
            cancellationToken);
    }

    private async Task RunDemoConsistencyForScanSymbolsAsync(Guid scanCycleId, CancellationToken cancellationToken)
    {
        if (!executionRuntimeOptionsValue.AllowInternalDemoExecution)
        {
            return;
        }

        var candidateSymbols = (await dbContext.MarketScannerCandidates
                .AsNoTracking()
                .Where(entity => entity.ScanCycleId == scanCycleId && !entity.IsDeleted && entity.Symbol != null)
                .Select(entity => entity.Symbol)
                .ToListAsync(cancellationToken))
            .Select(MarketDataSymbolNormalizer.Normalize)
            .Where(symbol => !string.IsNullOrWhiteSpace(symbol))
            .ToHashSet(StringComparer.Ordinal);

        if (candidateSymbols.Count == 0)
        {
            return;
        }

        var ownerUserIds = (await dbContext.TradingBots
                .AsNoTracking()
                .IgnoreQueryFilters()
                .Where(entity => entity.IsEnabled && !entity.IsDeleted && entity.Symbol != null)
                .Select(entity => new { entity.OwnerUserId, entity.Symbol })
                .ToListAsync(cancellationToken))
            .Where(entity => candidateSymbols.Contains(MarketDataSymbolNormalizer.Normalize(entity.Symbol!)))
            .Select(entity => entity.OwnerUserId)
            .Where(ownerUserId => !string.IsNullOrWhiteSpace(ownerUserId))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        foreach (var ownerUserId in ownerUserIds)
        {
            using var userScope = serviceScopeFactory.CreateScope();
            var dataScopeAccessor = userScope.ServiceProvider.GetRequiredService<IDataScopeContextAccessor>();
            using var scopeOverride = dataScopeAccessor.BeginScope(ownerUserId);
            var demoSessionService = userScope.ServiceProvider.GetService<IDemoSessionService>();
            if (demoSessionService is null)
            {
                continue;
            }

            await demoSessionService.RunConsistencyCheckAsync(ownerUserId, cancellationToken);
        }
    }

    private async Task<BotStrategyMatch?> ResolveBotMatchAsync(string symbol, CancellationToken cancellationToken)
    {
        var candidateBots = await dbContext.TradingBots
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(entity => entity.IsEnabled && !entity.IsDeleted && entity.Symbol != null)
            .OrderBy(entity => entity.OwnerUserId)
            .ThenBy(entity => entity.Id)
            .Select(entity => new
            {
                entity.Id,
                entity.OwnerUserId,
                entity.StrategyKey,
                entity.Symbol,
                entity.ExchangeAccountId,
                entity.DirectionMode,
                entity.Leverage,
                entity.MarginType
            })
            .ToListAsync(cancellationToken);

        foreach (var bot in candidateBots)
        {
            if (!string.Equals(MarketDataSymbolNormalizer.Normalize(bot.Symbol!), symbol, StringComparison.Ordinal))
            {
                continue;
            }

            var strategy = await dbContext.TradingStrategies
                .AsNoTracking()
                .IgnoreQueryFilters()
                .Where(entity =>
                    entity.OwnerUserId == bot.OwnerUserId &&
                    entity.StrategyKey == bot.StrategyKey &&
                    !entity.IsDeleted)
                .OrderByDescending(entity => entity.UpdatedDate)
                .ThenByDescending(entity => entity.CreatedDate)
                .FirstOrDefaultAsync(cancellationToken);

            if (strategy is null)
            {
                continue;
            }

            var strategyVersionId = (await StrategyRuntimeVersionSelection.ResolveAsync(
                dbContext,
                strategy.Id,
                cancellationToken))?.Id;

            if (!strategyVersionId.HasValue)
            {
                continue;
            }

            return new BotStrategyMatch(
                bot.Id,
                bot.OwnerUserId,
                bot.StrategyKey,
                bot.DirectionMode,
                bot.ExchangeAccountId,
                bot.Leverage,
                bot.MarginType,
                strategy.Id,
                strategyVersionId.Value);
        }

        return null;
    }
    private async Task<SymbolMetadataSnapshot?> ResolveSymbolMetadataAsync(string symbol, CancellationToken cancellationToken)
    {
        return await sharedSymbolRegistry.GetSymbolAsync(symbol, cancellationToken)
            ?? await marketDataService.GetSymbolMetadataAsync(symbol, cancellationToken)
            ?? await ResolveExchangeInfoSymbolMetadataAsync(symbol, cancellationToken);
    }

    private async Task<SymbolMetadataSnapshot?> ResolveExchangeInfoSymbolMetadataAsync(string symbol, CancellationToken cancellationToken)
    {
        if (exchangeInfoClient is null)
        {
            return null;
        }

        var snapshots = await exchangeInfoClient.GetSymbolMetadataAsync([symbol], cancellationToken);
        return snapshots.SingleOrDefault();
    }

    private async Task<(StrategyIndicatorSnapshot? IndicatorSnapshot, decimal? ReferencePrice)> ResolveMarketStateAsync(
        string symbol,
        string timeframe,
        CancellationToken cancellationToken)
    {
        var latestPrice = await marketDataService.GetLatestPriceAsync(symbol, cancellationToken);
        var indicatorSnapshot = await indicatorDataService.GetLatestAsync(symbol, timeframe, cancellationToken);

        if (indicatorSnapshot is null || indicatorSnapshot.State != IndicatorDataState.Ready)
        {
            var historicalCandles = await LoadHistoricalCandlesAsync(symbol, timeframe, cancellationToken);
            if (historicalCandles.Count > 0)
            {
                indicatorSnapshot = await indicatorDataService.PrimeAsync(symbol, timeframe, historicalCandles, cancellationToken);
                await RecordHistoricalMarketDataHeartbeatAsync(symbol, timeframe, historicalCandles, cancellationToken);
            }
        }

        var referencePrice = latestPrice?.Price is > 0m
            ? latestPrice.Price
            : await ResolveHistoricalReferencePriceAsync(symbol, timeframe, cancellationToken);

        return (
            indicatorSnapshot is not null && indicatorSnapshot.State == IndicatorDataState.Ready
                ? indicatorSnapshot
                : null,
            referencePrice);
    }

    private async Task<IReadOnlyCollection<MarketCandleSnapshot>> LoadHistoricalCandlesAsync(
        string symbol,
        string timeframe,
        CancellationToken cancellationToken)
    {
        var candleEntities = await dbContext.HistoricalMarketCandles
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(entity => !entity.IsDeleted && entity.Symbol == symbol && entity.Interval == timeframe)
            .OrderByDescending(entity => entity.CloseTimeUtc)
            .Take(botExecutionOptionsValue.PrimeHistoricalCandleCount)
            .ToListAsync(cancellationToken);

        return candleEntities
            .OrderBy(entity => entity.OpenTimeUtc)
            .Select(entity => new MarketCandleSnapshot(
                entity.Symbol,
                entity.Interval,
                NormalizeUtc(entity.OpenTimeUtc),
                NormalizeUtc(entity.CloseTimeUtc),
                entity.OpenPrice,
                entity.HighPrice,
                entity.LowPrice,
                entity.ClosePrice,
                entity.Volume,
                IsClosed: true,
                NormalizeUtc(entity.ReceivedAtUtc),
                entity.Source))
            .ToArray();
    }

    private async Task<decimal?> ResolveHistoricalReferencePriceAsync(string symbol, string timeframe, CancellationToken cancellationToken)
    {
        return await dbContext.HistoricalMarketCandles
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(entity => !entity.IsDeleted && entity.Symbol == symbol && entity.Interval == timeframe)
            .OrderByDescending(entity => entity.CloseTimeUtc)
            .Select(entity => (decimal?)entity.ClosePrice)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task RecordHistoricalMarketDataHeartbeatAsync(
        string symbol,
        string timeframe,
        IReadOnlyCollection<MarketCandleSnapshot> historicalCandles,
        CancellationToken cancellationToken)
    {
        var orderedCandles = historicalCandles
            .Where(snapshot => snapshot.IsClosed)
            .OrderBy(snapshot => snapshot.OpenTimeUtc)
            .ToArray();

        if (orderedCandles.Length == 0)
        {
            return;
        }

        var interval = ResolveIntervalDuration(timeframe);
        var latestSnapshot = orderedCandles[^1];
        var continuityGapCount = CountContinuityGaps(orderedCandles, interval);

        await dataLatencyCircuitBreaker.RecordHeartbeatAsync(
            new DataLatencyHeartbeat(
                Source: "market-scanner:historical-candles",
                DataTimestampUtc: latestSnapshot.CloseTimeUtc,
                GuardStateCode: continuityGapCount == 0 ? DegradedModeStateCode.Normal : DegradedModeStateCode.Stopped,
                GuardReasonCode: continuityGapCount == 0 ? DegradedModeReasonCode.None : DegradedModeReasonCode.CandleDataGapDetected,
                Symbol: symbol,
                Timeframe: timeframe,
                ExpectedOpenTimeUtc: latestSnapshot.OpenTimeUtc + interval,
                ContinuityGapCount: continuityGapCount),
            cancellationToken: cancellationToken);
    }

    private static StrategySignalSnapshot? SelectActionableEntrySignal(
        StrategySignalGenerationResult strategyResult,
        string symbol,
        string timeframe)
    {
        return strategyResult.Signals
            .Where(signal => signal.SignalType == StrategySignalType.Entry && signal.Symbol == symbol && signal.Timeframe == timeframe)
            .OrderByDescending(signal => signal.GeneratedAtUtc)
            .FirstOrDefault();
    }

    private async Task<DuplicateSignalResolution> ResolveDuplicateEntrySignalAsync(
        BotStrategyMatch botMatch,
        StrategyIndicatorSnapshot indicatorSnapshot,
        ExecutionEnvironment executionEnvironment,
        CancellationToken cancellationToken)
    {
        var signal = await dbContext.TradingStrategySignals
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(entity =>
                entity.OwnerUserId == botMatch.OwnerUserId &&
                entity.TradingStrategyVersionId == botMatch.TradingStrategyVersionId &&
                entity.SignalType == StrategySignalType.Entry &&
                entity.ExecutionEnvironment == executionEnvironment &&
                entity.Symbol == indicatorSnapshot.Symbol &&
                entity.Timeframe == indicatorSnapshot.Timeframe &&
                entity.IndicatorCloseTimeUtc == indicatorSnapshot.CloseTimeUtc &&
                !entity.IsDeleted)
            .OrderByDescending(entity => entity.GeneratedAtUtc)
            .ThenByDescending(entity => entity.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (signal is null)
        {
            return DuplicateSignalResolution.None;
        }

        var hasExistingExecutionRequest = await dbContext.ExecutionOrders
            .AsNoTracking()
            .IgnoreQueryFilters()
            .AnyAsync(
                entity =>
                    entity.OwnerUserId == botMatch.OwnerUserId &&
                    entity.StrategySignalId == signal.Id &&
                    entity.ExecutionEnvironment == executionEnvironment &&
                    !entity.IsDeleted,
                cancellationToken)
            || await dbContext.MarketScannerHandoffAttempts
                .AsNoTracking()
                .IgnoreQueryFilters()
                .AnyAsync(
                    entity =>
                        entity.OwnerUserId == botMatch.OwnerUserId &&
                        entity.StrategySignalId == signal.Id &&
                        entity.ExecutionEnvironment == executionEnvironment &&
                        entity.ExecutionRequestStatus == "Prepared" &&
                        !entity.IsDeleted,
                    cancellationToken);

        return new DuplicateSignalResolution(ToStrategySignalSnapshot(signal), hasExistingExecutionRequest);
    }

    private async Task<MarketScannerHandoffAttempt?> ResolveReplaySuppressedPreparedAttemptAsync(
        string ownerUserId,
        StrategySignalSnapshot strategySignal,
        PreparedExecutionContext executionContext,
        CancellationToken cancellationToken)
    {
        return await dbContext.MarketScannerHandoffAttempts
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(entity =>
                entity.OwnerUserId == ownerUserId &&
                entity.StrategySignalId == strategySignal.StrategySignalId &&
                entity.ExecutionRequestStatus == "Prepared" &&
                entity.ExecutionEnvironment == executionContext.Environment &&
                entity.ExecutionSide == executionContext.Side &&
                entity.ExecutionOrderType == executionContext.OrderType &&
                !entity.IsDeleted)
            .OrderByDescending(entity => entity.CreatedDate)
            .ThenByDescending(entity => entity.SelectedAtUtc)
            .ThenByDescending(entity => entity.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task<bool> HasExistingExecutionOrderIntentAsync(
        string ownerUserId,
        StrategySignalSnapshot strategySignal,
        PreparedExecutionContext executionContext,
        CancellationToken cancellationToken)
    {
        return await dbContext.ExecutionOrders
            .AsNoTracking()
            .IgnoreQueryFilters()
            .AnyAsync(
                entity =>
                    entity.OwnerUserId == ownerUserId &&
                    entity.StrategySignalId == strategySignal.StrategySignalId &&
                    entity.ExecutionEnvironment == executionContext.Environment &&
                    entity.Side == executionContext.Side &&
                    entity.OrderType == executionContext.OrderType &&
                    !entity.IsDeleted,
                cancellationToken);
    }

    private static StrategySignalVetoSnapshot? SelectVeto(
        StrategySignalGenerationResult strategyResult,
        string symbol,
        string timeframe)
    {
        return strategyResult.Vetoes
            .Where(veto => veto.SignalType == StrategySignalType.Entry && veto.Symbol == symbol && veto.Timeframe == timeframe)
            .OrderByDescending(veto => veto.EvaluatedAtUtc)
            .FirstOrDefault();
    }

    private static (string BlockerCode, string BlockerDetail, string StrategyOutcome, string GuardSummary) ResolveStrategyBlocker(
        StrategySignalGenerationResult strategyResult,
        StrategySignalVetoSnapshot? strategyVeto,
        string symbol,
        string timeframe)
    {
        var explainabilitySummary = BuildStrategyExplainabilitySummary(strategyResult.EvaluationReport);

        if (strategyVeto is not null)
        {
            return (
                "StrategyVetoed",
                strategyVeto.ConfidenceSnapshot.Summary,
                "Vetoed",
                Truncate($"StrategySignalVeto={strategyVeto.ConfidenceSnapshot.RiskReasonCode}; Symbol={symbol}; Timeframe={timeframe}; {explainabilitySummary}", 512) ?? $"StrategySignalVeto={strategyVeto.ConfidenceSnapshot.RiskReasonCode}; Symbol={symbol}; Timeframe={timeframe}");
        }

        if (strategyResult.SuppressedDuplicateCount > 0)
        {
            return (
                "DuplicateSignalSuppressed",
                "Scanner handoff skipped execution request creation because the strategy signal was duplicate-suppressed.",
                "SuppressedDuplicate",
                Truncate($"StrategySignalDuplicateSuppressed={strategyResult.SuppressedDuplicateCount}; Symbol={symbol}; Timeframe={timeframe}; {explainabilitySummary}", 512) ?? $"StrategySignalDuplicateSuppressed={strategyResult.SuppressedDuplicateCount}; Symbol={symbol}; Timeframe={timeframe}");
        }

        return (
            "NoActionableSignal",
            $"Scanner handoff did not find an actionable entry signal for {symbol} {timeframe}.",
            "NoSignalCandidate",
            Truncate($"StrategySignalOutcome=NoSignalCandidate; Symbol={symbol}; Timeframe={timeframe}; {explainabilitySummary}", 512) ?? $"StrategySignalOutcome=NoSignalCandidate; Symbol={symbol}; Timeframe={timeframe}");
    }

    private async Task<MarketScannerHandoffAttempt> PersistPreparedAttemptAsync(
        Guid scanCycleId,
        MarketScannerCandidate candidate,
        string ownerUserId,
        BotStrategyMatch botMatch,
        SymbolMetadataSnapshot? symbolMetadata,
        PreparedExecutionContext executionContext,
        DegradedModeSnapshot latencySnapshot,
        StrategySignalSnapshot strategySignal,
        StrategySignalGenerationResult strategyResult,
        ExecutionDispatchResult dispatchResult,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var attempt = CreateAttempt(
            scanCycleId,
            candidate,
            ownerUserId,
            botMatch,
            symbolMetadata,
            executionContext,
            strategySignal,
            strategyVeto: null,
            strategyDecisionOutcome: "Persisted",
            executionStatus: "Prepared",
            blockerCode: null,
            blockerDetail: null,
            correlationId,
            guardSummary: BuildPreparedGuardSummary(candidate.Symbol, strategyResult, strategySignal, latencySnapshot, dispatchResult));

        dbContext.MarketScannerHandoffAttempts.Add(attempt);
        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Market scanner handoff prepared. HandoffAttemptId={HandoffAttemptId} ScanCycleId={ScanCycleId} Symbol={Symbol} BotId={BotId} StrategySignalId={StrategySignalId}.",
            attempt.Id,
            scanCycleId,
            attempt.SelectedSymbol ?? "n/a",
            attempt.BotId,
            attempt.StrategySignalId);

        if (ultraDebugLogService is not null)
        {
            await ultraDebugLogService.WriteAsync(
                new UltraDebugLogEntry(
                    Category: "scanner.handoff",
                    EventName: "scanner_handoff_prepared",
                    Summary: $"Scanner handoff prepared {attempt.SelectedSymbol ?? "n/a"} for execution.",
                    CorrelationId: attempt.CorrelationId,
                    Symbol: attempt.SelectedSymbol,
                    ExecutionAttemptId: attempt.Id.ToString("N"),
                    StrategySignalId: attempt.StrategySignalId?.ToString("N"),
                    Detail: new
                    {
                        category = "handoff",
                        sourceLayer = nameof(MarketScannerHandoffService),
                        handoffAttemptId = attempt.Id,
                        scanCycleId,
                        symbol = attempt.SelectedSymbol,
                        timeframe = attempt.SelectedTimeframe,
                        botId = attempt.BotId,
                        strategySignalId = attempt.StrategySignalId,
                        strategySignalVetoId = attempt.StrategySignalVetoId,
                        decisionOutcome = attempt.StrategyDecisionOutcome,
                        decisionReasonType = "ExecutionPrepared",
                        decisionReasonCode = attempt.ExecutionRequestStatus,
                        selectedSymbol = attempt.SelectedSymbol,
                        strategyDecisionOutcome = attempt.StrategyDecisionOutcome,
                        executionRequestStatus = attempt.ExecutionRequestStatus,
                        executionSide = attempt.ExecutionSide?.ToString(),
                        executionEnvironment = attempt.ExecutionEnvironment?.ToString(),
                        blockerCode = attempt.BlockerCode,
                        blockerSummary = attempt.BlockerSummary,
                        guardSummary = attempt.GuardSummary
                    }),
                cancellationToken);
        }

        return attempt;
    }
    private async Task<MarketScannerHandoffAttempt> PersistBlockedAttemptAsync(
        Guid scanCycleId,
        MarketScannerCandidate? selectedCandidate,
        string? ownerUserId,
        BotStrategyMatch? botMatch,
        SymbolMetadataSnapshot? symbolMetadata,
        PreparedExecutionContext? executionContext,
        StrategySignalSnapshot? strategySignal,
        StrategySignalVetoSnapshot? strategyVeto,
        string strategyDecisionOutcome,
        string executionStatus,
        string blockerCode,
        string blockerDetail,
        string guardSummary,
        CancellationToken cancellationToken,
        string? correlationId = null,
        RiskVetoResult? riskEvaluation = null)
    {
        var replaySuppressedAttempt = await ResolveReplaySuppressedBlockedAttemptAsync(
            strategySignal,
            executionContext,
            blockerCode,
            cancellationToken);
        if (replaySuppressedAttempt is not null)
        {
            logger.LogInformation(
                "Market scanner handoff skipped duplicate blocked persistence replay. ExistingHandoffAttemptId={HandoffAttemptId} StrategySignalId={StrategySignalId} BlockerCode={BlockerCode}.",
                replaySuppressedAttempt.Id,
                replaySuppressedAttempt.StrategySignalId,
                blockerCode);
            return replaySuppressedAttempt;
        }

        var attempt = CreateAttempt(
            scanCycleId,
            selectedCandidate,
            ownerUserId,
            botMatch,
            symbolMetadata,
            executionContext,
            strategySignal,
            strategyVeto,
            strategyDecisionOutcome,
            executionStatus,
            blockerCode,
            blockerDetail,
            correlationId,
            guardSummary,
            riskEvaluation);

        dbContext.MarketScannerHandoffAttempts.Add(attempt);
        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Market scanner handoff blocked. HandoffAttemptId={HandoffAttemptId} ScanCycleId={ScanCycleId} Symbol={Symbol} Status={Status} BlockerCode={BlockerCode}.",
            attempt.Id,
            scanCycleId,
            attempt.SelectedSymbol ?? "n/a",
            executionStatus,
            blockerCode);

        if (ultraDebugLogService is not null)
        {
            await ultraDebugLogService.WriteAsync(
                new UltraDebugLogEntry(
                    Category: "scanner.handoff",
                    EventName: "scanner_handoff_blocked",
                    Summary: $"Scanner handoff blocked {attempt.SelectedSymbol ?? "n/a"} with blocker {attempt.BlockerCode ?? "n/a"}.",
                    CorrelationId: attempt.CorrelationId,
                    Symbol: attempt.SelectedSymbol,
                    ExecutionAttemptId: attempt.Id.ToString("N"),
                    StrategySignalId: attempt.StrategySignalId?.ToString("N"),
                    Detail: new
                    {
                        category = "handoff",
                        sourceLayer = nameof(MarketScannerHandoffService),
                        handoffAttemptId = attempt.Id,
                        scanCycleId,
                        symbol = attempt.SelectedSymbol,
                        timeframe = attempt.SelectedTimeframe,
                        botId = attempt.BotId,
                        strategySignalId = attempt.StrategySignalId,
                        strategySignalVetoId = attempt.StrategySignalVetoId,
                        decisionOutcome = attempt.ExecutionRequestStatus,
                        decisionReasonType = attempt.StrategySignalVetoId.HasValue ? "StrategyVeto" : "ExecutionGuard",
                        decisionReasonCode = attempt.BlockerCode,
                        selectedSymbol = attempt.SelectedSymbol,
                        executionRequestStatus = attempt.ExecutionRequestStatus,
                        blockerCode = attempt.BlockerCode,
                        blockerSummary = attempt.BlockerSummary,
                        strategyDecisionOutcome = attempt.StrategyDecisionOutcome,
                        executionEnvironment = attempt.ExecutionEnvironment?.ToString(),
                        guardSummary = attempt.GuardSummary
                    }),
                cancellationToken);
        }

        return attempt;
    }

    private async Task<MarketScannerHandoffAttempt?> ResolveReplaySuppressedBlockedAttemptAsync(
        StrategySignalSnapshot? strategySignal,
        PreparedExecutionContext? executionContext,
        string blockerCode,
        CancellationToken cancellationToken)
    {
        if (strategySignal is null ||
            string.IsNullOrWhiteSpace(blockerCode) ||
            !ReplaySuppressedBlockedAttemptCodes.Contains(blockerCode))
        {
            return null;
        }

        var effectiveEnvironment = executionContext?.Environment ?? strategySignal.Mode;
        return await dbContext.MarketScannerHandoffAttempts
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(entity =>
                entity.StrategySignalId == strategySignal.StrategySignalId &&
                entity.ExecutionRequestStatus == "Blocked" &&
                entity.ExecutionEnvironment == effectiveEnvironment &&
                entity.BlockerCode == blockerCode &&
                !entity.IsDeleted)
            .OrderByDescending(entity => entity.CreatedDate)
            .ThenByDescending(entity => entity.SelectedAtUtc)
            .ThenByDescending(entity => entity.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private MarketScannerHandoffAttempt CreateAttempt(
        Guid scanCycleId,
        MarketScannerCandidate? selectedCandidate,
        string? ownerUserId,
        BotStrategyMatch? botMatch,
        SymbolMetadataSnapshot? symbolMetadata,
        PreparedExecutionContext? executionContext,
        StrategySignalSnapshot? strategySignal,
        StrategySignalVetoSnapshot? strategyVeto,
        string strategyDecisionOutcome,
        string executionStatus,
        string? blockerCode,
        string? blockerDetail,
        string? correlationId,
        string? guardSummary,
        RiskVetoResult? riskEvaluation = null)
    {
        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        var selectedSymbol = selectedCandidate?.Symbol is null
            ? null
            : MarketDataSymbolNormalizer.Normalize(selectedCandidate.Symbol);

        var confidenceSnapshot = strategySignal?.ExplainabilityPayload.ConfidenceSnapshot ?? strategyVeto?.ConfidenceSnapshot;

        return new MarketScannerHandoffAttempt
        {
            Id = Guid.NewGuid(),
            ScanCycleId = scanCycleId,
            SelectedCandidateId = selectedCandidate?.Id,
            SelectedSymbol = selectedSymbol,
            SelectedTimeframe = selectedSymbol is null ? null : klineInterval,
            SelectedAtUtc = nowUtc,
            CandidateRank = selectedCandidate?.Rank,
            CandidateMarketScore = selectedCandidate?.MarketScore,
            CandidateScore = selectedCandidate?.Score,
            SelectionReason = BuildSelectionReason(selectedCandidate),
            OwnerUserId = ownerUserId?.Trim(),
            BotId = botMatch?.BotId,
            StrategyKey = botMatch?.StrategyKey,
            TradingStrategyId = botMatch?.TradingStrategyId,
            TradingStrategyVersionId = botMatch?.TradingStrategyVersionId,
            StrategySignalId = strategySignal?.StrategySignalId,
            StrategySignalVetoId = strategyVeto?.StrategySignalVetoId,
            StrategyDecisionOutcome = strategyDecisionOutcome,
            StrategyVetoReasonCode = strategyVeto?.ConfidenceSnapshot.RiskReasonCode.ToString(),
            StrategyScore = selectedCandidate?.StrategyScore
                ?? strategySignal?.ExplainabilityPayload.ConfidenceSnapshot.ScorePercentage
                ?? strategyVeto?.ConfidenceSnapshot.ScorePercentage,
            RiskOutcome = ResolveRiskOutcome(confidenceSnapshot, riskEvaluation, executionStatus),
            RiskVetoReasonCode = ResolveRiskReasonCode(confidenceSnapshot, riskEvaluation),
            RiskSummary = ResolveRiskSummary(confidenceSnapshot, riskEvaluation),
            RiskCurrentDailyLossPercentage = riskEvaluation?.Snapshot.CurrentDailyLossPercentage ?? confidenceSnapshot?.CurrentDailyLossPercentage,
            RiskMaxDailyLossPercentage = riskEvaluation?.Snapshot.MaxDailyLossPercentage ?? confidenceSnapshot?.MaxDailyLossPercentage,
            RiskCurrentWeeklyLossPercentage = riskEvaluation?.Snapshot.CurrentWeeklyLossPercentage ?? confidenceSnapshot?.CurrentWeeklyLossPercentage,
            RiskMaxWeeklyLossPercentage = riskEvaluation?.Snapshot.MaxWeeklyLossPercentage ?? confidenceSnapshot?.MaxWeeklyLossPercentage,
            RiskCurrentLeverage = riskEvaluation?.Snapshot.CurrentLeverage ?? confidenceSnapshot?.CurrentLeverage,
            RiskProjectedLeverage = riskEvaluation?.Snapshot.ProjectedLeverage ?? confidenceSnapshot?.ProjectedLeverage,
            RiskMaxLeverage = riskEvaluation?.Snapshot.MaxLeverage ?? confidenceSnapshot?.MaxLeverage,
            RiskCurrentSymbolExposurePercentage = riskEvaluation?.Snapshot.CurrentSymbolExposurePercentage ?? confidenceSnapshot?.CurrentSymbolExposurePercentage,
            RiskProjectedSymbolExposurePercentage = riskEvaluation?.Snapshot.ProjectedSymbolExposurePercentage ?? confidenceSnapshot?.ProjectedSymbolExposurePercentage,
            RiskMaxSymbolExposurePercentage = riskEvaluation?.Snapshot.MaxSymbolExposurePercentage ?? confidenceSnapshot?.MaxSymbolExposurePercentage,
            RiskCurrentOpenPositions = riskEvaluation?.Snapshot.OpenPositionCount ?? confidenceSnapshot?.CurrentOpenPositionCount,
            RiskProjectedOpenPositions = riskEvaluation?.Snapshot.ProjectedOpenPositionCount ?? confidenceSnapshot?.ProjectedOpenPositionCount,
            RiskMaxConcurrentPositions = riskEvaluation?.Snapshot.MaxConcurrentPositions ?? confidenceSnapshot?.MaxConcurrentPositions,
            RiskBaseAsset = riskEvaluation?.Snapshot.BaseAsset ?? confidenceSnapshot?.RiskBaseAsset,
            RiskCurrentCoinExposurePercentage = riskEvaluation?.Snapshot.CurrentCoinExposurePercentage ?? confidenceSnapshot?.CurrentCoinExposurePercentage,
            RiskProjectedCoinExposurePercentage = riskEvaluation?.Snapshot.ProjectedCoinExposurePercentage ?? confidenceSnapshot?.ProjectedCoinExposurePercentage,
            RiskMaxCoinExposurePercentage = riskEvaluation?.Snapshot.MaxCoinExposurePercentage ?? confidenceSnapshot?.MaxCoinExposurePercentage,
            ExecutionRequestStatus = executionStatus,
            ExecutionSide = executionContext?.Side,
            ExecutionOrderType = executionContext?.OrderType,
            ExecutionEnvironment = executionContext?.Environment,
            ExecutionQuantity = executionContext?.Quantity,
            ExecutionPrice = executionContext?.Price,
            BlockerCode = Truncate(blockerCode, 64),
            BlockerDetail = SanitizeBlockerDetail(blockerDetail),
            BlockerSummary = BuildBlockerSummary(executionStatus, blockerCode, blockerDetail, guardSummary),
            GuardSummary = Truncate(guardSummary, 512),
            CorrelationId = ResolvePersistedCorrelationId(correlationId),
            CompletedAtUtc = nowUtc
        };
    }

    private string BuildPreparedGuardSummary(
        string symbol,
        StrategySignalGenerationResult strategyResult,
        StrategySignalSnapshot strategySignal,
        DegradedModeSnapshot latencySnapshot,
        ExecutionDispatchResult dispatchResult)
    {
        var explainabilitySummary = BuildStrategyExplainabilitySummary(strategyResult.EvaluationReport);
        var signalSummary = string.IsNullOrWhiteSpace(strategySignal.ExplainabilityPayload.UiLog.Summary)
            ? strategySignal.ExplainabilityPayload.ConfidenceSnapshot.Summary
            : strategySignal.ExplainabilityPayload.UiLog.Summary;

        return Truncate(
            $"ExecutionGate=Allowed; UserExecutionOverride=Allowed; ExecutionDispatch=Dispatched; ExecutionOrderId={dispatchResult.Order.ExecutionOrderId:N}; ExecutionOrderState={dispatchResult.Order.State}; ExecutorKind={dispatchResult.Order.ExecutorKind}; DispatchDuplicate={dispatchResult.IsDuplicate}; Symbol={symbol}; Timeframe={klineInterval}; {BuildLatencyGuardSummarySnippet(latencySnapshot)}; StrategySignalSummary={signalSummary}; {explainabilitySummary}",
            512)
            ?? $"ExecutionGate=Allowed; UserExecutionOverride=Allowed; Symbol={symbol}; Timeframe={klineInterval}";
    }

    private static StrategySignalSnapshot ToStrategySignalSnapshot(TradingStrategySignal signal)
    {
        var indicatorSnapshot = DeserializeRequired<StrategyIndicatorSnapshot>(signal.IndicatorSnapshotJson);
        var evaluationResult = DeserializeRequired<StrategyEvaluationResult>(signal.RuleResultSnapshotJson);
        var confidenceSnapshot = DeserializeConfidenceSnapshot(signal.RiskEvaluationJson)
            ?? CreateUnavailableConfidenceSnapshot();

        return new StrategySignalSnapshot(
            signal.Id,
            signal.TradingStrategyId,
            signal.TradingStrategyVersionId,
            signal.StrategyVersionNumber,
            signal.StrategySchemaVersion,
            signal.SignalType,
            signal.ExecutionEnvironment,
            signal.Symbol,
            signal.Timeframe,
            signal.IndicatorOpenTimeUtc,
            signal.IndicatorCloseTimeUtc,
            signal.IndicatorReceivedAtUtc,
            signal.GeneratedAtUtc,
            new StrategySignalExplainabilityPayload(
                signal.ExplainabilitySchemaVersion,
                signal.TradingStrategyId,
                signal.TradingStrategyVersionId,
                signal.StrategyVersionNumber,
                signal.StrategySchemaVersion,
                signal.ExecutionEnvironment,
                indicatorSnapshot,
                evaluationResult,
                confidenceSnapshot,
                new StrategySignalLogExplainabilitySnapshot(
                    $"{signal.SignalType} signal",
                    confidenceSnapshot.Summary,
                    [],
                    []),
                new StrategySignalDuplicateSuppressionSnapshot(
                    true,
                    false,
                    CreateDuplicateFingerprint(
                        signal.TradingStrategyVersionId,
                        signal.SignalType,
                        signal.Symbol,
                        signal.Timeframe,
                        signal.IndicatorCloseTimeUtc))));
    }

    private static T DeserializeRequired<T>(string json)
    {
        var value = JsonSerializer.Deserialize<T>(json, StrategySignalSerializerOptions);

        return value is not null
            ? value
            : throw new InvalidOperationException($"Strategy signal JSON payload could not be deserialized as '{typeof(T).Name}'.");
    }

    private static StrategySignalConfidenceSnapshot? DeserializeConfidenceSnapshot(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<StrategySignalConfidenceSnapshot>(json, StrategySignalSerializerOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static StrategySignalConfidenceSnapshot CreateUnavailableConfidenceSnapshot()
    {
        return new StrategySignalConfidenceSnapshot(
            ScorePercentage: 0,
            Band: StrategySignalConfidenceBand.Low,
            MatchedRuleCount: 0,
            TotalRuleCount: 0,
            IsDeterministic: true,
            IsRiskApproved: false,
            IsVetoed: false,
            RiskReasonCode: RiskVetoReasonCode.None,
            IsVirtualRiskCheck: false,
            Summary: "Confidence snapshot unavailable.",
            AiEvaluation: null);
    }

    private static string CreateDuplicateFingerprint(
        Guid tradingStrategyVersionId,
        StrategySignalType signalType,
        string symbol,
        string timeframe,
        DateTime indicatorCloseTimeUtc)
    {
        return $"{tradingStrategyVersionId:N}:{signalType}:{symbol}:{timeframe}:{indicatorCloseTimeUtc:O}";
    }

    private static JsonSerializerOptions CreateStrategySignalSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());

        return options;
    }

    private static string BuildLatencyGuardSummarySnippet(DegradedModeSnapshot latencySnapshot)
    {
        return $"LatencyReason={latencySnapshot.ReasonCode}; LastCandleAtUtc={latencySnapshot.LatestDataTimestampAtUtc?.ToString("O") ?? "missing"}; DataAgeMs={latencySnapshot.LatestDataAgeMilliseconds?.ToString(CultureInfo.InvariantCulture) ?? "missing"}; ContinuityGapCount={latencySnapshot.LatestContinuityGapCount?.ToString(CultureInfo.InvariantCulture) ?? "missing"}; DecisionSourceLayer={(ExecutionDecisionDiagnostics.IsContinuityGuardReason(latencySnapshot.ReasonCode.ToString()) ? "continuity-validator" : "heartbeat-watchdog")}";
    }

    private static string BuildStrategyExplainabilitySummary(StrategyEvaluationReportSnapshot? report)
    {
        if (report is null)
        {
            return "StrategyExplainability=n/a";
        }

        var passedRules = report.PassedRules.Count == 0
            ? "none"
            : string.Join(" | ", report.PassedRules.Take(2));
        var failedRules = report.FailedRules.Count == 0
            ? "none"
            : string.Join(" | ", report.FailedRules.Take(2));
        var templateLabel = string.IsNullOrWhiteSpace(report.TemplateKey)
            ? "custom"
            : report.TemplateKey!.Trim();

        return FormattableString.Invariant(
            $"StrategyTemplate={templateLabel}; StrategyOutcome={report.Outcome}; StrategyScore={report.AggregateScore}; PassedRules={passedRules}; FailedRules={failedRules}; Explanation={report.ExplainabilitySummary}");
    }

    private static string? ResolveRiskOutcome(
        StrategySignalConfidenceSnapshot? confidenceSnapshot,
        RiskVetoResult? riskEvaluation,
        string executionStatus)
    {
        if (riskEvaluation is not null)
        {
            return riskEvaluation.IsVetoed ? "Vetoed" : "Allowed";
        }

        if (confidenceSnapshot is not null)
        {
            return confidenceSnapshot.IsVetoed ? "Vetoed" : "Allowed";
        }

        return string.Equals(executionStatus, "Prepared", StringComparison.Ordinal)
            ? "Allowed"
            : null;
    }

    private static string? ResolveRiskReasonCode(
        StrategySignalConfidenceSnapshot? confidenceSnapshot,
        RiskVetoResult? riskEvaluation)
    {
        if (riskEvaluation is not null)
        {
            return riskEvaluation.ReasonCode.ToString();
        }

        if (confidenceSnapshot is not null)
        {
            return confidenceSnapshot.RiskReasonCode.ToString();
        }

        return null;
    }

    private static string? ResolveRiskSummary(
        StrategySignalConfidenceSnapshot? confidenceSnapshot,
        RiskVetoResult? riskEvaluation)
    {
        return Truncate(
            riskEvaluation?.ReasonSummary ?? confidenceSnapshot?.RiskScopeSummary ?? confidenceSnapshot?.Summary,
            1024);
    }

    private static string BuildSelectionReason(MarketScannerCandidate? candidate)
    {
        if (candidate is null)
        {
            return "No eligible candidate available.";
        }

        return Truncate(
            FormattableString.Invariant(
                $"Top-ranked eligible candidate selected. Symbol={candidate.Symbol}; Rank={candidate.Rank?.ToString(CultureInfo.InvariantCulture) ?? "n/a"}; MarketScore={candidate.MarketScore.ToString("0.####", CultureInfo.InvariantCulture)}; StrategyScore={candidate.StrategyScore?.ToString(CultureInfo.InvariantCulture) ?? "n/a"}; CompositeScore={candidate.Score.ToString("0.####", CultureInfo.InvariantCulture)}; UniverseSource={candidate.UniverseSource}; {candidate.ScoringSummary ?? "StrategyScore=n/a"}"),
            512)
            ?? "Top-ranked eligible candidate selected.";
    }

    private decimal ResolveHandoffQuantity(SymbolMetadataSnapshot symbolMetadata, decimal referencePrice)
    {
        if (referencePrice <= 0m)
        {
            throw new ExecutionValidationException(
                "EntryQuantitySizingFailedClosed",
                $"Execution blocked because entry quantity sizing requires a positive reference price for '{symbolMetadata.Symbol}'.");
        }

        var candidateQuantity = symbolMetadata.MinQuantity ?? symbolMetadata.StepSize;
        if (candidateQuantity <= 0m)
        {
            throw new ExecutionValidationException(
                "EntryQuantitySizingFailedClosed",
                $"Execution blocked because entry quantity sizing could not be resolved for '{symbolMetadata.Symbol}'.");
        }

        var protectedMinNotional = ResolveProtectedMinNotional(symbolMetadata.MinNotional);
        if (protectedMinNotional is decimal minNotional)
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

        if (protectedMinNotional is decimal adjustedMinNotional &&
            (candidateQuantity * referencePrice) < adjustedMinNotional)
        {
            candidateQuantity = AlignUp(adjustedMinNotional / referencePrice, symbolMetadata.StepSize);
        }

        if (candidateQuantity <= 0m)
        {
            throw new ExecutionValidationException(
                "EntryQuantitySizingFailedClosed",
                $"Execution blocked because entry quantity sizing resolved to a non-positive value for '{symbolMetadata.Symbol}'.");
        }

        if (protectedMinNotional is decimal finalMinNotional &&
            (candidateQuantity * referencePrice) < finalMinNotional)
        {
            throw new ExecutionValidationException(
                "EntryNotionalSafetyBlocked",
                $"Execution blocked because protected entry notional {(candidateQuantity * referencePrice):0.########} is below the protected minimum notional {finalMinNotional:0.########} for '{symbolMetadata.Symbol}'.");
        }

        return candidateQuantity;
    }

    private decimal? ResolveProtectedMinNotional(decimal? minNotional)
    {
        if (minNotional is null || minNotional <= 0m)
        {
            return null;
        }

        var multiplier = botExecutionOptionsValue.MinNotionalSafetyMultiplier < 1m
            ? 1m
            : botExecutionOptionsValue.MinNotionalSafetyMultiplier;
        return decimal.Round(minNotional.Value * multiplier, 8, MidpointRounding.AwayFromZero);
    }

    private static decimal AlignUp(decimal value, decimal increment)
    {
        if (increment <= 0m)
        {
            return value;
        }

        var remainder = value % increment;
        return remainder == 0m ? value : value + (increment - remainder);
    }

    private static TimeSpan ResolveIntervalDuration(string timeframe)
    {
        var normalizedTimeframe = timeframe?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedTimeframe))
        {
            throw new ArgumentException("The timeframe is required.", nameof(timeframe));
        }

        var magnitudeText = normalizedTimeframe[..^1];
        var unit = normalizedTimeframe[^1];

        if (!int.TryParse(magnitudeText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var magnitude) || magnitude <= 0)
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

    private static int CountContinuityGaps(IReadOnlyList<MarketCandleSnapshot> orderedCandles, TimeSpan interval)
    {
        if (orderedCandles.Count < 2)
        {
            return 0;
        }

        var gapCount = 0;
        var previousOpenTimeUtc = NormalizeUtc(orderedCandles[0].OpenTimeUtc);

        for (var index = 1; index < orderedCandles.Count; index++)
        {
            var currentOpenTimeUtc = NormalizeUtc(orderedCandles[index].OpenTimeUtc);
            if (currentOpenTimeUtc <= previousOpenTimeUtc)
            {
                continue;
            }

            var expectedOpenTimeUtc = previousOpenTimeUtc + interval;
            if (currentOpenTimeUtc > expectedOpenTimeUtc)
            {
                gapCount += Math.Max(
                    1,
                    (int)Math.Round(
                        (currentOpenTimeUtc - expectedOpenTimeUtc).TotalMilliseconds / interval.TotalMilliseconds,
                        MidpointRounding.AwayFromZero));
            }

            previousOpenTimeUtc = currentOpenTimeUtc;
        }

        return gapCount;
    }

    private static DateTime NormalizeUtc(DateTime value)
    {
        return value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
    }

    private static string BuildGateContext(
        Guid scanCycleId,
        MarketScannerCandidate candidate,
        BotStrategyMatch botMatch,
        ExecutionEnvironment executionEnvironment,
        BotExecutionPilotOptions pilotOptions,
        string marginType,
        decimal leverage)
    {
        var context = $"ScannerHandoff=True | ScanCycleId={scanCycleId:N} | CandidateId={candidate.Id:N} | CandidateRank={candidate.Rank?.ToString(CultureInfo.InvariantCulture) ?? "n/a"} | BotId={botMatch.BotId:N}";
        if (executionEnvironment != ExecutionEnvironment.Live || !pilotOptions.PilotActivationEnabled)
        {
            return context;
        }

        return FormattableString.Invariant(
            $"{context} | DevelopmentFuturesTestnetPilot=True | PilotActivationEnabled={pilotOptions.PilotActivationEnabled} | PilotMarginType={marginType} | PilotLeverage={leverage:0.########}");
    }

    private static async Task<ExecutionEnvironment> ResolveHandoffExecutionEnvironmentAsync(
        ITradingModeResolver tradingModeResolver,
        BotStrategyMatch botMatch,
        CancellationToken cancellationToken)
    {
        var resolution = await tradingModeResolver.ResolveAsync(
            new TradingModeResolutionRequest(
                botMatch.OwnerUserId,
                botMatch.BotId,
                botMatch.StrategyKey),
            cancellationToken);

        return resolution.EffectiveMode;
    }

    private string ResolveQuoteAsset(string symbol, SymbolMetadataSnapshot? symbolMetadata)
    {
        if (!string.IsNullOrWhiteSpace(symbolMetadata?.QuoteAsset))
        {
            return symbolMetadata.QuoteAsset.Trim().ToUpperInvariant();
        }

        return allowedQuoteAssets.FirstOrDefault(item => symbol.EndsWith(item, StringComparison.Ordinal)) ?? "USDT";
    }

    private string ResolveBaseAsset(string symbol, SymbolMetadataSnapshot? symbolMetadata)
    {
        if (!string.IsNullOrWhiteSpace(symbolMetadata?.BaseAsset))
        {
            return symbolMetadata.BaseAsset.Trim().ToUpperInvariant();
        }

        var quoteAsset = ResolveQuoteAsset(symbol, symbolMetadata);
        return symbol.EndsWith(quoteAsset, StringComparison.Ordinal) && symbol.Length > quoteAsset.Length
            ? symbol[..^quoteAsset.Length]
            : symbol;
    }

    private async Task<string> ResolveAttemptCorrelationIdAsync(
        StrategySignalSnapshot strategySignal,
        CancellationToken cancellationToken)
    {
        if (traceService is not null)
        {
            var decisionTrace = await traceService.GetDecisionTraceByStrategySignalIdAsync(
                strategySignal.StrategySignalId,
                cancellationToken);
            if (!string.IsNullOrWhiteSpace(decisionTrace?.CorrelationId))
            {
                return decisionTrace.CorrelationId;
            }
        }

        return ResolveAmbientCorrelationId();
    }

    private string ResolvePersistedCorrelationId(string? correlationId)
    {
        return string.IsNullOrWhiteSpace(correlationId)
            ? ResolveAmbientCorrelationId()
            : correlationId.Trim();
    }

    private string ResolveAmbientCorrelationId()
    {
        var scopedCorrelationId = correlationContextAccessor?.Current?.CorrelationId;
        if (!string.IsNullOrWhiteSpace(scopedCorrelationId))
        {
            return scopedCorrelationId.Trim();
        }

        var activityTraceId = Activity.Current?.TraceId.ToString();
        return string.IsNullOrWhiteSpace(activityTraceId)
            ? Guid.NewGuid().ToString("N")
            : activityTraceId;
    }

    private static string BuildScannerHandoffIdempotencyKey(
        StrategySignalSnapshot strategySignal,
        PreparedExecutionContext executionContext)
    {
        return $"scanner-handoff:{strategySignal.StrategySignalId:N}:{executionContext.Environment}:{executionContext.Side}";
    }

    private static string? BuildBlockerSummary(
        string executionStatus,
        string? blockerCode,
        string? blockerDetail,
        string? guardSummary)
    {
        if (string.Equals(executionStatus, "Prepared", StringComparison.Ordinal))
        {
            return "Allowed: execution request prepared.";
        }

        var normalizedCode = string.IsNullOrWhiteSpace(blockerCode)
            ? "Blocked"
            : blockerCode.Trim();
        var humanSummary = ExecutionDecisionDiagnostics.ExtractHumanSummary(blockerDetail)
            ?? ExecutionDecisionDiagnostics.ExtractHumanSummary(guardSummary);

        return Truncate(
            string.IsNullOrWhiteSpace(humanSummary)
                ? $"{normalizedCode}: scanner handoff blocked execution."
                : $"{normalizedCode}: {humanSummary}",
            256);
    }

    private static string? SanitizeBlockerDetail(string? blockerDetail)
    {
        if (string.IsNullOrWhiteSpace(blockerDetail))
        {
            return null;
        }

        return Truncate(blockerDetail.Trim(), 512);
    }

    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength];
    }

    private sealed record BotStrategyMatch(
        Guid BotId,
        string OwnerUserId,
        string StrategyKey,
        TradingBotDirectionMode DirectionMode,
        Guid? ExchangeAccountId,
        decimal? Leverage,
        string? MarginType,
        Guid TradingStrategyId,
        Guid TradingStrategyVersionId);

    private async Task<decimal> ResolveCurrentNetQuantityAsync(
        BotStrategyMatch botMatch,
        string symbol,
        ExecutionEnvironment executionEnvironment,
        CancellationToken cancellationToken)
    {
        var normalizedSymbol = NormalizePositionSymbol(symbol);

        if (UsesInternalDemoExecution(executionEnvironment))
        {
            return await dbContext.DemoPositions
                .AsNoTracking()
                .IgnoreQueryFilters()
                .Where(entity =>
                    entity.OwnerUserId == botMatch.OwnerUserId &&
                    entity.BotId == botMatch.BotId &&
                    entity.Symbol == normalizedSymbol &&
                    !entity.IsDeleted)
                .SumAsync(entity => entity.Quantity, cancellationToken);
        }

        return await LivePositionTruthResolver.ResolveNetQuantityAsync(
            dbContext,
            botMatch.OwnerUserId,
            ExchangeDataPlane.Futures,
            botMatch.ExchangeAccountId,
            normalizedSymbol,
            cancellationToken);
    }

    private async Task<string?> ResolveEntryHysteresisSummaryAsync(
        BotStrategyMatch botMatch,
        string symbol,
        StrategyTradeDirection entryDirection,
        decimal? referencePrice,
        CancellationToken cancellationToken)
    {
        if (!botExecutionOptionsValue.EnableEntryHysteresis ||
            !IsActionableDirection(entryDirection) ||
            !botMatch.ExchangeAccountId.HasValue)
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
                entity.OwnerUserId == botMatch.OwnerUserId &&
                entity.ExchangeAccountId == botMatch.ExchangeAccountId &&
                entity.BotId == botMatch.BotId &&
                entity.Symbol == NormalizePositionSymbol(symbol) &&
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
        var resolvedExitPrice = latestExitOrder.AverageFillPrice.GetValueOrDefault() > 0m
            ? latestExitOrder.AverageFillPrice.GetValueOrDefault()
            : latestExitOrder.Price;
        var cooldownMinutes = botExecutionOptionsValue.ResolveEntryHysteresisCooldownMinutes(entryDirection);
        var reentryBufferPercentage = botExecutionOptionsValue.ResolveEntryHysteresisReentryBufferPercentage(entryDirection);

        if (cooldownMinutes > 0 &&
            latestExitOrder.CreatedDate.AddMinutes(cooldownMinutes) > nowUtc)
        {
            return $"Entry signal was skipped because entry hysteresis cooldown is still active after the last filled {entryDirection.ToString().ToLowerInvariant()} exit. LastExitAtUtc={latestExitOrder.CreatedDate:O}; CooldownMinutes={cooldownMinutes}.";
        }

        if (!referencePrice.HasValue || resolvedExitPrice <= 0m)
        {
            return null;
        }

        if (entryDirection == StrategyTradeDirection.Long &&
            TryResolveUpperPriceBoundary(resolvedExitPrice, reentryBufferPercentage, out var longReentryThresholdPrice) &&
            referencePrice.Value <= longReentryThresholdPrice)
        {
            return $"Entry signal was skipped because hysteresis re-entry buffer is not yet satisfied for a long rearm. LastExitPrice={resolvedExitPrice:0.########}; ReferencePrice={referencePrice.Value:0.########}; RearmThresholdPrice={longReentryThresholdPrice:0.########}.";
        }

        if (entryDirection == StrategyTradeDirection.Short &&
            TryResolveLowerPriceBoundary(resolvedExitPrice, reentryBufferPercentage, out var shortReentryThresholdPrice) &&
            referencePrice.Value >= shortReentryThresholdPrice)
        {
            return $"Entry signal was skipped because hysteresis re-entry buffer is not yet satisfied for a short rearm. LastExitPrice={resolvedExitPrice:0.########}; ReferencePrice={referencePrice.Value:0.########}; RearmThresholdPrice={shortReentryThresholdPrice:0.########}.";
        }

        return null;
    }

    private bool UsesInternalDemoExecution(ExecutionEnvironment executionEnvironment)
    {
        return executionEnvironment == ExecutionEnvironment.Demo &&
               executionRuntimeOptionsValue.AllowInternalDemoExecution;
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

    private static decimal NormalizeDecimal(decimal value)
    {
        return decimal.Round(value, 8, MidpointRounding.AwayFromZero);
    }

    private static string NormalizePositionSymbol(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().ToUpperInvariant();
    }

    private static bool IsActionableDirection(StrategyTradeDirection direction)
    {
        return direction is StrategyTradeDirection.Long or StrategyTradeDirection.Short;
    }

    private static string ResolveSameDirectionEntrySuppressedBlockerCode(StrategyTradeDirection entryDirection)
    {
        return entryDirection switch
        {
            StrategyTradeDirection.Long => "SameDirectionLongEntrySuppressed",
            StrategyTradeDirection.Short => "SameDirectionShortEntrySuppressed",
            _ => "SameDirectionEntrySuppressed"
        };
    }

    private static string ResolveEntryHysteresisActiveBlockerCode(StrategyTradeDirection entryDirection)
    {
        return entryDirection switch
        {
            StrategyTradeDirection.Long => "LongEntryHysteresisActive",
            StrategyTradeDirection.Short => "ShortEntryHysteresisActive",
            _ => "EntryHysteresisActive"
        };
    }

    private bool TryResolvePilotExecutionParameters(
        BotStrategyMatch botMatch,
        out decimal leverage,
        out string marginType,
        out string? failureCode)
    {
        leverage = botMatch.Leverage ?? 1m;
        marginType = string.IsNullOrWhiteSpace(botMatch.MarginType)
            ? "ISOLATED"
            : botMatch.MarginType.Trim().ToUpperInvariant();
        failureCode = null;

        if (leverage != 1m &&
            !(hostEnvironment?.IsDevelopment() == true && botExecutionOptionsValue.AllowNonOneLeverageForClockDriftSmoke))
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

    private static string BuildPilotParameterFailureDetail(string? failureCode)
    {
        return failureCode switch
        {
            "PilotLeverageMustBeOne" => "Scanner handoff blocked because pilot bot leverage must resolve to 1x.",
            "PilotMarginTypeMustBeIsolated" => "Scanner handoff blocked because pilot bot margin type must resolve to ISOLATED.",
            _ => "Scanner handoff blocked because pilot execution parameters are invalid."
        };
    }

    private sealed record DuplicateSignalResolution(
        StrategySignalSnapshot? Signal,
        bool HasExistingExecutionRequest)
    {
        public static DuplicateSignalResolution None { get; } = new(null, false);
    }

    private readonly record struct PreparedExecutionContext(
        ExecutionOrderSide Side,
        ExecutionOrderType OrderType,
        ExecutionEnvironment Environment,
        decimal Quantity,
        decimal Price);

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

    private static bool TryResolveEntryDirectionModeBlock(
        TradingBotDirectionMode directionMode,
        string symbol,
        StrategyTradeDirection entryDirection,
        out string? summary)
    {
        summary = null;

        if (entryDirection is not StrategyTradeDirection.Long and not StrategyTradeDirection.Short)
        {
            return false;
        }

        var blocked = directionMode switch
        {
            TradingBotDirectionMode.LongOnly => entryDirection == StrategyTradeDirection.Short,
            TradingBotDirectionMode.ShortOnly => entryDirection == StrategyTradeDirection.Long,
            _ => false
        };

        if (!blocked)
        {
            return false;
        }

        summary = $"Execution blocked because bot direction mode {directionMode} does not allow {entryDirection.ToString().ToLowerInvariant()} entries for {symbol}.";
        return true;
    }
}
