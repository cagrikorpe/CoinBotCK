using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CoinBot.Application.Abstractions.Administration;
using CoinBot.Application.Abstractions.Execution;
using CoinBot.Application.Abstractions.MarketData;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Jobs;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CoinBot.Infrastructure.Administration;

public sealed class AdminManualCloseService(
    ApplicationDbContext dbContext,
    IExecutionEngine executionEngine,
    ITradingModeResolver tradingModeResolver,
    IMarketDataService marketDataService,
    IOptions<BotExecutionPilotOptions>? botExecutionPilotOptions = null,
    TimeProvider? timeProvider = null) : IAdminManualCloseService
{
    private const string ManualCloseIntent = "ManualExitCloseOnly";
    private const string ManualCloseStrategyKey = "__admin_manual_close__";
    private const string ManualCloseNoOpenPositionCode = "ManualCloseNoOpenPosition";
    private const string ManualClosePrivatePlaneStaleCode = "ManualCloseBlockedPrivatePlaneStale";
    private const string ManualCloseCredentialUnavailableCode = "ManualCloseCredentialUnavailable";
    private const string ManualCloseReadOnlyAccountCode = "ManualCloseReadOnlyAccount";
    private const string ManualCloseOwnershipMismatchCode = "ManualCloseOwnershipMismatch";
    private const string ManualCloseEnvironmentInvalidCode = "ManualCloseEnvironmentInvalid";
    private const string ManualCloseBotMissingCode = "ManualCloseBotNotFound";
    private const string ManualCloseBotConfigurationInvalidCode = "ManualCloseBotConfigurationInvalid";
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

    private readonly BotExecutionPilotOptions pilotOptionsValue = botExecutionPilotOptions?.Value ?? new BotExecutionPilotOptions();
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public async Task<AdminManualCloseResult> CloseAsync(
        AdminManualCloseRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        NormalizeRequired(request.ActorUserId, nameof(request.ActorUserId), 450);
        var normalizedExecutionActor = NormalizeRequired(request.ExecutionActor, nameof(request.ExecutionActor), 256);
        var normalizedCorrelationId = NormalizeOptional(request.CorrelationId, 128);
        var bot = await dbContext.TradingBots
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleOrDefaultAsync(
                entity => entity.Id == request.BotId &&
                          !entity.IsDeleted,
                cancellationToken);

        if (bot is null)
        {
            return Fail(
                ManualCloseBotMissingCode,
                "Manual close blocked because the bot could not be resolved.",
                "Bot bulunamadi.");
        }

        if (bot.ExchangeAccountId is not Guid exchangeAccountId ||
            string.IsNullOrWhiteSpace(bot.Symbol))
        {
            return Fail(
                ManualCloseBotConfigurationInvalidCode,
                "Manual close blocked because the bot is missing exchange account or symbol configuration.",
                "Bot icin exchange hesabi veya sembol tanimli degil.");
        }

        var resolvedMode = await tradingModeResolver.ResolveAsync(
            new TradingModeResolutionRequest(
                bot.OwnerUserId,
                bot.Id,
                bot.StrategyKey),
            cancellationToken);

        if (resolvedMode.EffectiveMode != ExecutionEnvironment.BinanceTestnet)
        {
            return Fail(
                ManualCloseEnvironmentInvalidCode,
                $"Manual close blocked because the effective environment is {resolvedMode.EffectiveMode}, not BinanceTestnet.",
                "Manual close yalnizca BinanceTestnet icin kullanilabilir.");
        }

        var exchangeAccount = await dbContext.ExchangeAccounts
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleOrDefaultAsync(
                entity => entity.Id == exchangeAccountId &&
                          !entity.IsDeleted,
                cancellationToken);

        if (exchangeAccount is null)
        {
            return Fail(
                ManualCloseCredentialUnavailableCode,
                "Manual close blocked because the exchange account could not be resolved.",
                "Exchange hesabi bulunamadi.");
        }

        if (exchangeAccount.OwnerUserId != bot.OwnerUserId)
        {
            return Fail(
                ManualCloseOwnershipMismatchCode,
                "Manual close blocked because exchange account ownership does not match the target bot.",
                "Bot ve exchange hesap sahipligi eslesmiyor.");
        }

        if (exchangeAccount.IsReadOnly)
        {
            return Fail(
                ManualCloseReadOnlyAccountCode,
                "Manual close blocked because the exchange account is read-only.",
                "Salt-okunur exchange hesabi ile manuel close yapilamaz.");
        }

        if (exchangeAccount.CredentialStatus != ExchangeCredentialStatus.Active)
        {
            return Fail(
                ManualCloseCredentialUnavailableCode,
                $"Manual close blocked because credential status is {exchangeAccount.CredentialStatus}.",
                "Exchange credential aktif degil.");
        }

        var syncState = await dbContext.ExchangeAccountSyncStates
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(entity =>
                entity.ExchangeAccountId == exchangeAccountId &&
                entity.OwnerUserId == bot.OwnerUserId &&
                entity.Plane == ExchangeDataPlane.Futures &&
                !entity.IsDeleted)
            .OrderByDescending(entity => entity.UpdatedDate)
            .ThenByDescending(entity => entity.CreatedDate)
            .FirstOrDefaultAsync(cancellationToken);

        if (IsPrivatePlaneStale(syncState))
        {
            return Fail(
                ManualClosePrivatePlaneStaleCode,
                BuildPrivatePlaneStaleSummary(syncState),
                "Private plane stale oldugu icin manuel close bloklandi.");
        }

        var position = await dbContext.ExchangePositions
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(entity =>
                entity.ExchangeAccountId == exchangeAccountId &&
                entity.OwnerUserId == bot.OwnerUserId &&
                entity.Plane == ExchangeDataPlane.Futures &&
                entity.Symbol == bot.Symbol &&
                entity.Quantity != 0m &&
                !entity.IsDeleted)
            .OrderByDescending(entity => entity.UpdatedDate)
            .ThenByDescending(entity => entity.CreatedDate)
            .FirstOrDefaultAsync(cancellationToken);

        if (position is null || position.Quantity == 0m)
        {
            return Fail(
                ManualCloseNoOpenPositionCode,
                $"ManualClose=True; ExecutionIntent={ManualCloseIntent}; Symbol={bot.Symbol}; Environment=BinanceTestnet; ReasonCode={ManualCloseNoOpenPositionCode}",
                "Acik pozisyon bulunamadi.");
        }

        if (position.OwnerUserId != bot.OwnerUserId ||
            position.ExchangeAccountId != exchangeAccountId)
        {
            return Fail(
                ManualCloseOwnershipMismatchCode,
                "Manual close blocked because exchange position ownership does not match the target bot.",
                "Pozisyon sahipligi bot ile eslesmiyor.");
        }

        var closeQuantity = Math.Abs(position.Quantity);
        if (closeQuantity <= 0m)
        {
            return Fail(
                ManualCloseNoOpenPositionCode,
                $"ManualClose=True; ExecutionIntent={ManualCloseIntent}; Symbol={position.Symbol}; Environment=BinanceTestnet; ReasonCode={ManualCloseNoOpenPositionCode}",
                "Kapatilacak acik pozisyon bulunamadi.");
        }

        var closeSide = ResolveCloseSide(position.Quantity);
        var positionDirection = position.Quantity > 0m ? "Long" : "Short";
        var closePrice = await ResolveClosePriceAsync(position, cancellationToken);
        var (baseAsset, quoteAsset) = await ResolveAssetsAsync(position.Symbol, cancellationToken);
        var context = BuildContext(
            normalizedExecutionActor,
            position.Symbol,
            position.Quantity,
            closeQuantity,
            closeSide,
            positionDirection);

        var command = new ExecutionCommand(
            normalizedExecutionActor,
            bot.OwnerUserId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            StrategySignalType.Exit,
            ManualCloseStrategyKey,
            position.Symbol,
            "admin",
            baseAsset,
            quoteAsset,
            closeSide,
            ExecutionOrderType.Market,
            closeQuantity,
            closePrice,
            BotId: bot.Id,
            ExchangeAccountId: exchangeAccountId,
            IdempotencyKey: BuildIdempotencyKey(bot.Id, exchangeAccountId, position, closeSide),
            CorrelationId: normalizedCorrelationId,
            Context: context,
            ReduceOnly: true,
            AdministrativeOverride: false,
            Plane: ExchangeDataPlane.Futures,
            RequestedEnvironment: ExecutionEnvironment.BinanceTestnet);

        var dispatchResult = await executionEngine.DispatchAsync(command, cancellationToken);
        var order = dispatchResult.Order;
        var outcomeCode = ResolveResultCode(order.FailureCode, order.State, order.SubmittedToBroker);
        var summary = BuildResultSummary(context, order, outcomeCode);
        var userMessage = BuildUserMessage(order, dispatchResult.IsDuplicate, outcomeCode);
        var isSuccess = order.SubmittedToBroker &&
                        order.State != ExecutionOrderState.Rejected &&
                        order.State != ExecutionOrderState.Failed;

        return new AdminManualCloseResult(
            isSuccess,
            outcomeCode,
            summary,
            userMessage,
            order,
            dispatchResult.IsDuplicate);
    }

    private static string ResolveResultCode(string? failureCode, ExecutionOrderState state, bool submittedToBroker)
    {
        if (!string.IsNullOrWhiteSpace(failureCode))
        {
            return failureCode switch
            {
                nameof(ExecutionGateBlockedReason.PrivatePlaneStale) => ManualClosePrivatePlaneStaleCode,
                "ReduceOnlyWithoutOpenPosition" or "ReduceOnlyQuantityInvalid" or "ReduceOnlyQuantityExceedsOpenPosition" or "ReduceOnlyWouldIncreaseExposure" => ManualCloseNoOpenPositionCode,
                _ => failureCode
            };
        }

        return submittedToBroker
            ? "ManualCloseSubmitted"
            : state.ToString();
    }

    private static string BuildUserMessage(ExecutionOrderSnapshot order, bool isDuplicate, string outcomeCode)
    {
        if (isDuplicate)
        {
            return "Ayni manual close istegi zaten kuyrukta.";
        }

        if (order.SubmittedToBroker &&
            order.State != ExecutionOrderState.Rejected &&
            order.State != ExecutionOrderState.Failed)
        {
            return "Reduce-only manual close emri gonderildi.";
        }

        return outcomeCode switch
        {
            ManualCloseNoOpenPositionCode => "Acik pozisyon bulunamadi.",
            ManualClosePrivatePlaneStaleCode => "Private plane stale oldugu icin manuel close bloklandi.",
            ManualCloseCredentialUnavailableCode => "Exchange credential aktif degil.",
            ManualCloseReadOnlyAccountCode => "Salt-okunur exchange hesabi ile manuel close yapilamaz.",
            ManualCloseOwnershipMismatchCode => "Bot, hesap veya pozisyon sahipligi eslesmiyor.",
            ManualCloseEnvironmentInvalidCode => "Manual close yalnizca BinanceTestnet icin kullanilabilir.",
            _ => order.FailureDetail ?? "Manual close basarisiz oldu."
        };
    }

    private static string BuildResultSummary(string context, ExecutionOrderSnapshot order, string outcomeCode)
    {
        return $"{context} | OutcomeCode={outcomeCode} | OrderState={order.State} | SubmittedToBroker={order.SubmittedToBroker} | ExecutorKind={order.ExecutorKind} | ExecutionEnvironment={order.ExecutionEnvironment}";
    }

    private string BuildPrivatePlaneStaleSummary(ExchangeAccountSyncState? syncState)
    {
        var lastSyncAtUtc = ResolveLastPrivateSyncAtUtc(syncState);
        var ageMs = ResolveAgeMilliseconds(clock.GetUtcNow().UtcDateTime, lastSyncAtUtc);
        return $"ManualClose=True; ExecutionIntent={ManualCloseIntent}; ExitSource=Manual; Environment=BinanceTestnet; ReasonCode={ManualClosePrivatePlaneStaleCode}; PrivateStreamState={syncState?.PrivateStreamConnectionState.ToString() ?? "Unavailable"}; DriftStatus={syncState?.DriftStatus.ToString() ?? "Unavailable"}; LastPrivateSyncAtUtc={lastSyncAtUtc?.ToString("O") ?? "missing"}; PrivatePlaneAgeMs={ageMs?.ToString(CultureInfo.InvariantCulture) ?? "missing"}; PrivatePlaneThresholdMs={checked(pilotOptionsValue.PrivatePlaneFreshnessThresholdSeconds * 1000)}";
    }

    private bool IsPrivatePlaneStale(ExchangeAccountSyncState? syncState)
    {
        if (syncState is null)
        {
            return true;
        }

        var lastSyncAtUtc = ResolveLastPrivateSyncAtUtc(syncState);
        var ageMs = ResolveAgeMilliseconds(clock.GetUtcNow().UtcDateTime, lastSyncAtUtc);

        return !lastSyncAtUtc.HasValue ||
               syncState.PrivateStreamConnectionState != ExchangePrivateStreamConnectionState.Connected ||
               syncState.DriftStatus != ExchangeStateDriftStatus.InSync ||
               !ageMs.HasValue ||
               ageMs.Value > checked(pilotOptionsValue.PrivatePlaneFreshnessThresholdSeconds * 1000);
    }

    private static DateTime? ResolveLastPrivateSyncAtUtc(ExchangeAccountSyncState? syncState)
    {
        if (syncState is null)
        {
            return null;
        }

        DateTime? latest = null;
        Consider(syncState.LastPrivateStreamEventAtUtc);
        Consider(syncState.LastBalanceSyncedAtUtc);
        Consider(syncState.LastPositionSyncedAtUtc);
        Consider(syncState.LastStateReconciledAtUtc);
        return latest;

        void Consider(DateTime? value)
        {
            if (!value.HasValue)
            {
                return;
            }

            var normalizedValue = value.Value.Kind == DateTimeKind.Utc
                ? value.Value
                : DateTime.SpecifyKind(value.Value, DateTimeKind.Utc);

            if (!latest.HasValue || normalizedValue > latest.Value)
            {
                latest = normalizedValue;
            }
        }
    }

    private static int? ResolveAgeMilliseconds(DateTime nowUtc, DateTime? observedAtUtc)
    {
        if (!observedAtUtc.HasValue)
        {
            return null;
        }

        var deltaMilliseconds = (nowUtc - observedAtUtc.Value).TotalMilliseconds;
        if (deltaMilliseconds <= 0)
        {
            return 0;
        }

        return deltaMilliseconds >= int.MaxValue
            ? int.MaxValue
            : (int)Math.Round(deltaMilliseconds, MidpointRounding.AwayFromZero);
    }

    private async Task<decimal> ResolveClosePriceAsync(
        ExchangePosition position,
        CancellationToken cancellationToken)
    {
        var latestPrice = await marketDataService.GetLatestPriceAsync(position.Symbol, cancellationToken);
        if (latestPrice is not null && latestPrice.Price > 0m)
        {
            return latestPrice.Price;
        }

        if (position.EntryPrice > 0m)
        {
            return position.EntryPrice;
        }

        if (position.BreakEvenPrice > 0m)
        {
            return position.BreakEvenPrice;
        }

        return 1m;
    }

    private async Task<(string BaseAsset, string QuoteAsset)> ResolveAssetsAsync(
        string symbol,
        CancellationToken cancellationToken)
    {
        var metadata = await marketDataService.GetSymbolMetadataAsync(symbol, cancellationToken);
        if (metadata is not null)
        {
            return (metadata.BaseAsset, metadata.QuoteAsset);
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

    private static string BuildContext(
        string executionActor,
        string symbol,
        decimal openPositionQuantity,
        decimal closeQuantity,
        ExecutionOrderSide closeSide,
        string positionDirection)
    {
        return string.Join(
            " | ",
            "DevelopmentFuturesTestnetPilot=True",
            $"ExecutionIntent={ManualCloseIntent}",
            "ManualClose=True",
            "ExitSource=Manual",
            $"CloseSide={closeSide}",
            "ReduceOnly=True",
            "AutoReverse=False",
            $"OpenPositionQuantity={openPositionQuantity.ToString("0.########", CultureInfo.InvariantCulture)}",
            $"CloseQuantity={closeQuantity.ToString("0.########", CultureInfo.InvariantCulture)}",
            $"Symbol={symbol}",
            "Environment=BinanceTestnet",
            $"PositionDirection={positionDirection}",
            $"ManualCloseRequestedBy={executionActor}");
    }

    private static string BuildIdempotencyKey(
        Guid botId,
        Guid exchangeAccountId,
        ExchangePosition position,
        ExecutionOrderSide closeSide)
    {
        var payload = string.Join(
            "|",
            "manual-close",
            botId.ToString("N"),
            exchangeAccountId.ToString("N"),
            position.Symbol,
            position.Quantity.ToString("0.##################", CultureInfo.InvariantCulture),
            position.EntryPrice.ToString("0.##################", CultureInfo.InvariantCulture),
            position.ExchangeUpdatedAtUtc.ToString("O"),
            position.SyncedAtUtc.ToString("O"),
            closeSide);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return $"manual_close_{Convert.ToHexStringLower(hash)[..40]}";
    }

    private static ExecutionOrderSide ResolveCloseSide(decimal quantity)
    {
        return quantity > 0m
            ? ExecutionOrderSide.Sell
            : ExecutionOrderSide.Buy;
    }

    private static AdminManualCloseResult Fail(string code, string summary, string userMessage)
    {
        return new AdminManualCloseResult(false, code, Truncate(summary, 512) ?? code, userMessage);
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
            : normalizedValue[..maxLength];
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
}
