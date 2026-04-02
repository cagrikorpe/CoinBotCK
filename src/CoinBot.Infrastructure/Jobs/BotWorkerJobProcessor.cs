using System.Globalization;
using CoinBot.Application.Abstractions.Execution;
using CoinBot.Application.Abstractions.Indicators;
using CoinBot.Application.Abstractions.MarketData;
using CoinBot.Application.Abstractions.Strategies;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Execution;
using CoinBot.Infrastructure.MarketData;
using CoinBot.Infrastructure.Observability;
using CoinBot.Infrastructure.Persistence;
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
    ICorrelationContextAccessor correlationContextAccessor,
    IOptions<BotExecutionPilotOptions> options,
    IHostEnvironment hostEnvironment,
    TimeProvider timeProvider,
    ILogger<BotWorkerJobProcessor> logger) : IBotWorkerJobProcessor
{
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

        if (!hostEnvironment.IsDevelopment())
        {
            logger.LogWarning("Bot execution pilot is restricted to Development. BotId={BotId}", bot.Id);
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
            logger.LogInformation(
                "Bot execution pilot skipped BotId {BotId} because a reference price is unavailable for {Symbol}.",
                bot.Id,
                normalizedSymbol);
            return BackgroundJobProcessResult.RetryableFailure("ReferencePriceUnavailable");
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
                        marketState.IndicatorSnapshot)),
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

        var signal = await ResolveActionableSignalAsync(
            signalGenerationResult,
            publishedVersion.Id,
            normalizedSymbol,
            timeframe,
            marketState.IndicatorSnapshot.CloseTimeUtc,
            cancellationToken);

        if (signal is null)
        {
            logger.LogInformation(
                "Bot execution pilot found no actionable entry signal for BotId {BotId}. Signals={SignalCount} Vetoes={VetoCount} Suppressed={SuppressedDuplicateCount}",
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

        decimal quantity;

        try
        {
            quantity = ResolvePilotQuantity(symbolMetadata, marketState.ReferencePrice.Value);
        }
        catch (ExecutionValidationException exception)
        {
            logger.LogWarning(
                exception,
                "Bot execution pilot rejected BotId {BotId} because pilot quantity resolution failed for {Symbol}.",
                bot.Id,
                normalizedSymbol);
            return BackgroundJobProcessResult.PermanentFailure("PilotQuantityInvalid");
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
                    Side: ExecutionOrderSide.Buy,
                    OrderType: ExecutionOrderType.Market,
                    Quantity: quantity,
                    Price: marketState.ReferencePrice.Value,
                    BotId: bot.Id,
                    ExchangeAccountId: exchangeAccount.Id,
                    IsDemo: false,
                    IdempotencyKey: $"{idempotencyKey}:{signal.StrategySignalId:N}",
                    CorrelationId: null,
                    ParentCorrelationId: null,
                    Context: BuildPilotExecutionContext(marginType!, leverage!.Value)),
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
            return BackgroundJobProcessResult.PermanentFailure(nameof(ExecutionValidationException));
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

        return await dbContext.TradingStrategyVersions
            .IgnoreQueryFilters()
            .Where(entity =>
                entity.TradingStrategyId == strategy.Id &&
                entity.Status == StrategyVersionStatus.Published &&
                !entity.IsDeleted)
            .OrderByDescending(entity => entity.VersionNumber)
            .FirstOrDefaultAsync(cancellationToken);
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

    private async Task<(StrategyIndicatorSnapshot? IndicatorSnapshot, decimal? ReferencePrice)> ResolveMarketStateAsync(
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
            return (indicatorSnapshot, latestPrice.Price);
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
            return (indicatorSnapshot, latestPrice?.Price);
        }

        if (historicalCandles.Count == 0)
        {
            return (indicatorSnapshot, latestPrice?.Price);
        }

        indicatorSnapshot = await indicatorDataService.PrimeAsync(
            symbol,
            timeframe,
            historicalCandles,
            cancellationToken);

        var historicalReferencePrice = historicalCandles
            .OrderByDescending(snapshot => snapshot.CloseTimeUtc)
            .Select(snapshot => (decimal?)snapshot.ClosePrice)
            .FirstOrDefault();

        return (
            indicatorSnapshot is not null && indicatorSnapshot.State == IndicatorDataState.Ready
                ? indicatorSnapshot
                : null,
            latestPrice?.Price ?? historicalReferencePrice);
    }

    private async Task<StrategySignalSnapshot?> ResolveActionableSignalAsync(
        StrategySignalGenerationResult generationResult,
        Guid tradingStrategyVersionId,
        string symbol,
        string timeframe,
        DateTime indicatorCloseTimeUtc,
        CancellationToken cancellationToken)
    {
        var entrySignal = generationResult.Signals
            .Where(signal =>
                signal.SignalType == StrategySignalType.Entry &&
                signal.Symbol == symbol &&
                signal.Timeframe == timeframe)
            .OrderByDescending(signal => signal.GeneratedAtUtc)
            .FirstOrDefault();

        if (entrySignal is not null)
        {
            return entrySignal;
        }

        if (generationResult.SuppressedDuplicateCount == 0)
        {
            return null;
        }

        var persistedSignal = await dbContext.TradingStrategySignals
            .IgnoreQueryFilters()
            .Where(entity =>
                entity.TradingStrategyVersionId == tradingStrategyVersionId &&
                entity.SignalType == StrategySignalType.Entry &&
                entity.Symbol == symbol &&
                entity.Timeframe == timeframe &&
                entity.IndicatorCloseTimeUtc == indicatorCloseTimeUtc &&
                !entity.IsDeleted)
            .OrderByDescending(entity => entity.GeneratedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

        return persistedSignal is null
            ? null
            : await strategySignalService.GetAsync(persistedSignal.Id, cancellationToken);
    }

    private static bool TryResolvePilotExecutionParameters(
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

        if (leverage != 1m)
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
                nameof(ExecutionValidationException),
                StringComparison.Ordinal) => BackgroundJobProcessResult.PermanentFailure(
                    dispatchResult.Order.FailureCode ?? nameof(ExecutionValidationException)),
            ExecutionOrderState.Failed => BackgroundJobProcessResult.RetryableFailure(
                dispatchResult.Order.FailureCode ?? "ExecutionFailed"),
            _ => BackgroundJobProcessResult.RetryableFailure("UnexpectedExecutionState")
        };
    }

    private static string BuildPilotExecutionContext(string marginType, decimal leverage)
    {
        return FormattableString.Invariant(
            $"DevelopmentFuturesTestnetPilot=True | PilotMarginType={marginType} | PilotLeverage={leverage:0.########}");
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
