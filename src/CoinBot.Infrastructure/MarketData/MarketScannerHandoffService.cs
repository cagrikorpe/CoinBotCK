
using System.Globalization;
using CoinBot.Application.Abstractions.DataScope;
using CoinBot.Application.Abstractions.Execution;
using CoinBot.Application.Abstractions.Indicators;
using CoinBot.Application.Abstractions.MarketData;
using CoinBot.Application.Abstractions.Risk;
using CoinBot.Application.Abstractions.Strategies;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Execution;
using CoinBot.Infrastructure.Jobs;
using CoinBot.Infrastructure.Persistence;
using CoinBot.Infrastructure.Strategies;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
    ILogger<MarketScannerHandoffService> logger)
{
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
                guardSummary: "Handoff disabled by MarketData:Scanner:HandoffEnabled.",
                cancellationToken);
        }

        var candidates = await dbContext.MarketScannerCandidates
            .AsNoTracking()
            .Where(entity => entity.ScanCycleId == scanCycleId && entity.IsEligible)
            .OrderBy(entity => entity.Rank ?? int.MaxValue)
            .ThenByDescending(entity => entity.Score)
            .ThenBy(entity => entity.Symbol)
            .ToListAsync(cancellationToken);

        if (candidates.Count == 0)
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
                    cancellationToken);
                continue;
            }

            await marketDataService.TrackSymbolAsync(symbol, cancellationToken);
            await indicatorDataService.TrackSymbolAsync(symbol, cancellationToken);

            var symbolMetadata = await ResolveSymbolMetadataAsync(symbol, cancellationToken);
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
                    cancellationToken);
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
                    cancellationToken);
                continue;
            }
            var executionContext = new PreparedExecutionContext(
                Side: ExecutionOrderSide.Buy,
                OrderType: ExecutionOrderType.Market,
                Environment: botExecutionOptionsValue.SignalEvaluationMode,
                Quantity: ResolveHandoffQuantity(symbolMetadata, marketState.ReferencePrice.Value),
                Price: marketState.ReferencePrice.Value);

            using var userScope = serviceScopeFactory.CreateScope();
            var dataScopeAccessor = userScope.ServiceProvider.GetRequiredService<IDataScopeContextAccessor>();
            using var scopeOverride = dataScopeAccessor.BeginScope(ownerBotMatch.OwnerUserId);
            var strategySignalService = userScope.ServiceProvider.GetRequiredService<IStrategySignalService>();
            var executionGate = userScope.ServiceProvider.GetRequiredService<IExecutionGate>();
            var userExecutionOverrideGuard = userScope.ServiceProvider.GetRequiredService<IUserExecutionOverrideGuard>();

            var strategyResult = await strategySignalService.GenerateAsync(
                new GenerateStrategySignalsRequest(
                    ownerBotMatch.TradingStrategyVersionId,
                    new StrategyEvaluationContext(botExecutionOptionsValue.SignalEvaluationMode, marketState.IndicatorSnapshot)),
                cancellationToken);

            var strategySignal = SelectActionableEntrySignal(strategyResult, symbol, klineInterval);
            var strategyVeto = SelectVeto(strategyResult, symbol, klineInterval);

            if (strategySignal is null)
            {
                var strategyBlocker = ResolveStrategyBlocker(strategyResult, strategyVeto, symbol, klineInterval);
                latestAttempt = await PersistBlockedAttemptAsync(
                    scanCycleId,
                    candidate,
                    ownerBotMatch.OwnerUserId,
                    ownerBotMatch,
                    symbolMetadata,
                    executionContext,
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

            try
            {
                var gateContext = BuildGateContext(scanCycleId, candidate, ownerBotMatch);
                var correlationId = CreateCorrelationId();

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
                        Timeframe: klineInterval),
                    cancellationToken);
                var latencySnapshot = await dataLatencyCircuitBreaker.GetSnapshotAsync(
                    correlationId,
                    symbol,
                    klineInterval,
                    cancellationToken);

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
                    cancellationToken);

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
                    guardSummary: Truncate(
                        $"UserExecutionOverrideGuard={overrideEvaluation.BlockCode ?? "Blocked"}; Symbol={symbol}; Timeframe={klineInterval}; {BuildLatencyGuardSummarySnippet(latencySnapshot)}; RiskSummary={overrideEvaluation.RiskEvaluation?.ReasonSummary ?? "n/a"}",
                        512) ?? $"UserExecutionOverrideGuard={overrideEvaluation.BlockCode ?? "Blocked"}; Symbol={symbol}; Timeframe={klineInterval}",
                    cancellationToken: cancellationToken,
                    riskEvaluation: overrideEvaluation.RiskEvaluation);
                continue;
            }

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
                    guardSummary: $"ExecutionGate={exception.Reason}; Symbol={symbol}; Timeframe={klineInterval}",
                    cancellationToken);
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
                    guardSummary: $"ScannerHandoffException={exception.GetType().Name}; Symbol={symbol}; Timeframe={klineInterval}",
                    cancellationToken);
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
                entity.Symbol
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
                strategy.Id,
                strategyVersionId.Value);
        }

        return null;
    }
    private async Task<SymbolMetadataSnapshot?> ResolveSymbolMetadataAsync(string symbol, CancellationToken cancellationToken)
    {
        return await sharedSymbolRegistry.GetSymbolAsync(symbol, cancellationToken)
            ?? await marketDataService.GetSymbolMetadataAsync(symbol, cancellationToken);
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
            guardSummary: BuildPreparedGuardSummary(candidate.Symbol, strategyResult, strategySignal, latencySnapshot));

        dbContext.MarketScannerHandoffAttempts.Add(attempt);
        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Market scanner handoff prepared. HandoffAttemptId={HandoffAttemptId} ScanCycleId={ScanCycleId} Symbol={Symbol} BotId={BotId} StrategySignalId={StrategySignalId}.",
            attempt.Id,
            scanCycleId,
            attempt.SelectedSymbol ?? "n/a",
            attempt.BotId,
            attempt.StrategySignalId);

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
        RiskVetoResult? riskEvaluation = null)
    {
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

        return attempt;
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
            GuardSummary = Truncate(guardSummary, 512),
            CorrelationId = CreateCorrelationId(),
            CompletedAtUtc = nowUtc
        };
    }

    private string BuildPreparedGuardSummary(
        string symbol,
        StrategySignalGenerationResult strategyResult,
        StrategySignalSnapshot strategySignal,
        DegradedModeSnapshot latencySnapshot)
    {
        var explainabilitySummary = BuildStrategyExplainabilitySummary(strategyResult.EvaluationReport);
        var signalSummary = string.IsNullOrWhiteSpace(strategySignal.ExplainabilityPayload.UiLog.Summary)
            ? strategySignal.ExplainabilityPayload.ConfidenceSnapshot.Summary
            : strategySignal.ExplainabilityPayload.UiLog.Summary;

        return Truncate(
            $"ExecutionGate=Allowed; UserExecutionOverride=Allowed; Symbol={symbol}; Timeframe={klineInterval}; {BuildLatencyGuardSummarySnippet(latencySnapshot)}; StrategySignalSummary={signalSummary}; {explainabilitySummary}",
            512)
            ?? $"ExecutionGate=Allowed; UserExecutionOverride=Allowed; Symbol={symbol}; Timeframe={klineInterval}";
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

    private decimal ResolveHandoffQuantity(SymbolMetadataSnapshot? symbolMetadata, decimal referencePrice)
    {
        if (symbolMetadata is null)
        {
            return 1m;
        }

        var candidateQuantity = symbolMetadata.MinQuantity ?? symbolMetadata.StepSize;
        if (candidateQuantity <= 0m)
        {
            return 1m;
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

        return candidateQuantity > 0m ? candidateQuantity : 1m;
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

    private static string BuildGateContext(Guid scanCycleId, MarketScannerCandidate candidate, BotStrategyMatch botMatch)
    {
        return $"ScannerHandoff=True | ScanCycleId={scanCycleId:N} | CandidateId={candidate.Id:N} | CandidateRank={candidate.Rank?.ToString(CultureInfo.InvariantCulture) ?? "n/a"} | BotId={botMatch.BotId:N}";
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

    private static string CreateCorrelationId()
    {
        return Guid.NewGuid().ToString("N");
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
        Guid TradingStrategyId,
        Guid TradingStrategyVersionId);

    private readonly record struct PreparedExecutionContext(
        ExecutionOrderSide Side,
        ExecutionOrderType OrderType,
        ExecutionEnvironment Environment,
        decimal Quantity,
        decimal Price);
}




