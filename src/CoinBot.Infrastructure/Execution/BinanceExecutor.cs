using System.Globalization;
using CoinBot.Application.Abstractions.Autonomy;
using CoinBot.Application.Abstractions.Execution;
using CoinBot.Application.Abstractions.ExchangeCredentials;
using CoinBot.Application.Abstractions.MarketData;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Exchange;
using CoinBot.Infrastructure.MarketData;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CoinBot.Infrastructure.Execution;

public sealed class BinanceExecutor(
    ApplicationDbContext dbContext,
    IExchangeCredentialService exchangeCredentialService,
    IBinancePrivateRestClient privateRestClient,
    ILogger<BinanceExecutor> logger,
    IDependencyCircuitBreakerStateManager? dependencyCircuitBreakerStateManager = null,
    IMarketDataService? marketDataService = null,
    IBinanceExchangeInfoClient? exchangeInfoClient = null) : IExecutionTargetExecutor
{
    private const string BreakerActor = "system:order-execution";

    public ExecutionOrderExecutorKind Kind => ExecutionOrderExecutorKind.Binance;

    public async Task<ExecutionTargetDispatchResult> DispatchAsync(
        ExecutionOrder order,
        ExecutionCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(command);

        var exchangeAccountId = command.ExchangeAccountId
            ?? throw new InvalidOperationException("Live execution requires an exchange account.");
        var exchangeAccount = await dbContext.ExchangeAccounts
            .IgnoreQueryFilters()
            .SingleOrDefaultAsync(
                entity => entity.Id == exchangeAccountId &&
                          !entity.IsDeleted,
                cancellationToken)
            ?? throw new InvalidOperationException($"Exchange account '{exchangeAccountId}' was not found.");

        if (!string.Equals(exchangeAccount.OwnerUserId, command.OwnerUserId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Exchange account owner does not match the execution command owner.");
        }

        if (!string.Equals(exchangeAccount.ExchangeName, "Binance", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Only Binance exchange accounts are supported by the live execution core.");
        }

        if (exchangeAccount.IsReadOnly)
        {
            throw new InvalidOperationException("Live execution is blocked because the exchange account is read-only.");
        }

        try
        {
            var credentialAccess = await exchangeCredentialService.GetAsync(
                new ExchangeCredentialAccessRequest(
                    exchangeAccountId,
                    command.Actor,
                    ExchangeCredentialAccessPurpose.Execution,
                    order.RootCorrelationId),
                cancellationToken);
            var symbolMetadata = await ResolveSymbolMetadataAsync(command.Symbol, cancellationToken);
            ValidateOrderPreflight(command, symbolMetadata);
            await ValidateFuturesMarginAvailabilityAsync(exchangeAccountId, command, symbolMetadata, cancellationToken);

            if (TryResolveDevelopmentFuturesPilot(command.Context, out var marginType, out var leverage))
            {
                await privateRestClient.EnsureMarginTypeAsync(
                    exchangeAccountId,
                    command.Symbol,
                    marginType!,
                    credentialAccess.ApiKey,
                    credentialAccess.ApiSecret,
                    cancellationToken);
                await privateRestClient.EnsureLeverageAsync(
                    exchangeAccountId,
                    command.Symbol,
                    leverage!.Value,
                    credentialAccess.ApiKey,
                    credentialAccess.ApiSecret,
                    cancellationToken);
            }

            var placementResult = await privateRestClient.PlaceOrderAsync(
                new BinanceOrderPlacementRequest(
                    exchangeAccountId,
                    command.Symbol,
                    command.Side,
                    command.OrderType,
                    command.Quantity,
                    command.Price,
                    BuildClientOrderId(order.Id, command.Context),
                    credentialAccess.ApiKey,
                    credentialAccess.ApiSecret,
                    order.IdempotencyKey,
                    order.RootCorrelationId,
                    ExecutionAttemptId: null,
                    order.Id,
                    command.OwnerUserId,
                    command.ReduceOnly),
                cancellationToken);

            if (dependencyCircuitBreakerStateManager is not null)
            {
                await dependencyCircuitBreakerStateManager.RecordSuccessAsync(
                    new DependencyCircuitBreakerSuccessRequest(
                        DependencyCircuitBreakerKind.OrderExecution,
                        BreakerActor,
                        order.RootCorrelationId),
                    cancellationToken);
            }

            logger.LogInformation(
                "Binance executor submitted order {ExecutionOrderId} for {Symbol}.",
                order.Id,
                command.Symbol);

            return new ExecutionTargetDispatchResult(
                placementResult.OrderId,
                placementResult.SubmittedAtUtc,
                BuildDispatchDetail(placementResult),
                placementResult.Snapshot);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (dependencyCircuitBreakerStateManager is not null &&
                ShouldRecordBreakerFailure(exception))
            {
                await dependencyCircuitBreakerStateManager.RecordFailureAsync(
                    new DependencyCircuitBreakerFailureRequest(
                        DependencyCircuitBreakerKind.OrderExecution,
                        BreakerActor,
                        ResolveFailureCode(exception),
                        Truncate(exception.Message, 512) ?? "Order execution failed.",
                        order.RootCorrelationId),
                    cancellationToken);
            }

            throw;
        }
    }

    private async Task<SymbolMetadataSnapshot> ResolveSymbolMetadataAsync(
        string symbol,
        CancellationToken cancellationToken)
    {
        var cachedMetadata = marketDataService is null
            ? null
            : await marketDataService.GetSymbolMetadataAsync(symbol, cancellationToken);

        if (cachedMetadata is not null)
        {
            return cachedMetadata;
        }

        if (exchangeInfoClient is null)
        {
            throw new ExecutionValidationException(
                "SymbolMetadataUnavailable",
                $"Symbol metadata for '{symbol}' is unavailable.");
        }

        var snapshots = await exchangeInfoClient.GetSymbolMetadataAsync([symbol], cancellationToken);
        var resolvedSnapshot = snapshots.SingleOrDefault();

        return resolvedSnapshot
            ?? throw new ExecutionValidationException(
                "SymbolMetadataUnavailable",
                $"Symbol metadata for '{symbol}' is unavailable.");
    }

    private async Task ValidateFuturesMarginAvailabilityAsync(
        Guid exchangeAccountId,
        ExecutionCommand command,
        SymbolMetadataSnapshot metadata,
        CancellationToken cancellationToken)
    {
        if (command.ReduceOnly ||
            !TryResolveDevelopmentFuturesPilot(command.Context, out _, out var leverage))
        {
            return;
        }

        var guardAsset = ResolveGuardAsset(command, metadata);
        var balance = await dbContext.ExchangeBalances
            .AsNoTracking()
            .IgnoreQueryFilters()
            .SingleOrDefaultAsync(
                entity => entity.ExchangeAccountId == exchangeAccountId &&
                          entity.Plane == ExchangeDataPlane.Futures &&
                          entity.Asset == guardAsset &&
                          !entity.IsDeleted,
                cancellationToken);

        if (balance is null)
        {
            return;
        }

        var availableMargin = ResolveAvailableMargin(balance);
        var requiredMargin = ResolveRequiredMargin(command, leverage!.Value);

        if (availableMargin + 0.000000000000000001m >= requiredMargin)
        {
            return;
        }

        throw new ExecutionValidationException(
            "FuturesMarginInsufficient",
            $"Execution blocked because available {guardAsset} futures margin {FormatDecimal(availableMargin)} is below required initial margin {FormatDecimal(requiredMargin)} for {command.Symbol} at leverage {FormatDecimal(leverage.Value)}.");
    }
    private static void ValidateOrderPreflight(ExecutionCommand command, SymbolMetadataSnapshot metadata)
    {
        if (!metadata.IsTradingEnabled)
        {
            throw new ExecutionValidationException(
                "SymbolTradingDisabled",
                $"Symbol '{command.Symbol}' is not trading-enabled.");
        }

        if (metadata.MinQuantity is decimal minQuantity && command.Quantity < minQuantity)
        {
            throw new ExecutionValidationException(
                "OrderQuantityBelowMinimum",
                $"Order quantity {command.Quantity} is below the minimum quantity {minQuantity} for '{command.Symbol}'.");
        }

        if (!IsAligned(command.Quantity, metadata.StepSize))
        {
            throw new ExecutionValidationException(
                "OrderQuantityStepSizeMismatch",
                $"Order quantity {command.Quantity} does not align with step size {metadata.StepSize} for '{command.Symbol}'.");
        }

        if (metadata.QuantityPrecision is int quantityPrecision &&
            CountFractionalDigits(command.Quantity) > quantityPrecision)
        {
            throw new ExecutionValidationException(
                "OrderQuantityPrecisionExceeded",
                $"Order quantity {command.Quantity} exceeds quantity precision {quantityPrecision} for '{command.Symbol}'.");
        }

        if (!command.ReduceOnly &&
            metadata.MinNotional is decimal minNotional &&
            (command.Quantity * command.Price) < minNotional)
        {
            throw new ExecutionValidationException(
                "OrderNotionalBelowMinimum",
                $"Order notional {(command.Quantity * command.Price)} is below the minimum notional {minNotional} for '{command.Symbol}'.");
        }

        if (command.OrderType != ExecutionOrderType.Limit)
        {
            return;
        }

        if (!IsAligned(command.Price, metadata.TickSize))
        {
            throw new ExecutionValidationException(
                "LimitPriceTickSizeMismatch",
                $"Limit price {command.Price} does not align with tick size {metadata.TickSize} for '{command.Symbol}'.");
        }

        if (metadata.PricePrecision is int pricePrecision &&
            CountFractionalDigits(command.Price) > pricePrecision)
        {
            throw new ExecutionValidationException(
                "LimitPricePrecisionExceeded",
                $"Limit price {command.Price} exceeds price precision {pricePrecision} for '{command.Symbol}'.");
        }
    }

    private static bool ShouldRecordBreakerFailure(Exception exception)
    {
        return exception is not ExecutionValidationException validationException ||
               !IsNonDependencyValidationFailure(validationException.ReasonCode);
    }

    private static bool IsNonDependencyValidationFailure(string? reasonCode)
    {
        return reasonCode is "OrderQuantityBelowMinimum" or
            "OrderQuantityStepSizeMismatch" or
            "OrderQuantityPrecisionExceeded" or
            "OrderNotionalBelowMinimum" or
            "LimitPriceTickSizeMismatch" or
            "LimitPricePrecisionExceeded" or
            "SymbolTradingDisabled" or
            "FuturesMarginInsufficient" or
            "ReduceOnlyWithoutOpenPosition" or
            "ReduceOnlyWouldIncreaseExposure" or
            "ReduceOnlyQuantityExceedsOpenPosition";
    }

    private static string ResolveGuardAsset(ExecutionCommand command, SymbolMetadataSnapshot metadata)
    {
        return string.IsNullOrWhiteSpace(metadata.QuoteAsset)
            ? command.QuoteAsset
            : metadata.QuoteAsset;
    }

    private static decimal ResolveRequiredMargin(ExecutionCommand command, decimal leverage)
    {
        var normalizedLeverage = leverage < 1m ? 1m : leverage;
        return ResolveReferenceNotional(command) / normalizedLeverage;
    }

    private static decimal ResolveReferenceNotional(ExecutionCommand command)
    {
        return command.QuoteQuantity ?? (command.Quantity * command.Price);
    }

    private static decimal ResolveAvailableMargin(ExchangeBalance? balance)
    {
        if (balance is null)
        {
            return 0m;
        }

        if (balance.AvailableBalance.HasValue)
        {
            return Math.Max(0m, balance.AvailableBalance.Value);
        }

        if (balance.MaxWithdrawAmount.HasValue)
        {
            return Math.Max(0m, balance.MaxWithdrawAmount.Value);
        }

        if (balance.CrossWalletBalance != 0m)
        {
            return Math.Max(0m, balance.CrossWalletBalance);
        }

        var lockedBalance = balance.LockedBalance ?? 0m;
        return Math.Max(0m, balance.WalletBalance - lockedBalance);
    }
    private static bool TryResolveDevelopmentFuturesPilot(
        string? context,
        out string? marginType,
        out decimal? leverage)
    {
        marginType = null;
        leverage = null;

        if (!TryReadBooleanFlag(context, "DevelopmentFuturesTestnetPilot"))
        {
            return false;
        }

        marginType = ReadContextValue(context, "PilotMarginType");
        leverage = decimal.TryParse(
            ReadContextValue(context, "PilotLeverage"),
            CultureInfo.InvariantCulture,
            out var parsedLeverage)
            ? parsedLeverage
            : null;

        return !string.IsNullOrWhiteSpace(marginType) && leverage.HasValue;
    }

    private static string BuildClientOrderId(Guid executionOrderId, string? context)
    {
        return TryReadBooleanFlag(context, "DevelopmentFuturesTestnetPilot")
            ? ExecutionClientOrderId.CreateDevelopmentFuturesPilot(executionOrderId)
            : ExecutionClientOrderId.Create(executionOrderId);
    }

    private static string BuildDispatchDetail(BinanceOrderPlacementResult placementResult)
    {
        if (placementResult.Snapshot is null)
        {
            return $"ClientOrderId={placementResult.ClientOrderId}";
        }

        return string.Join(
            "; ",
            [
                $"ClientOrderId={placementResult.ClientOrderId}",
                $"Plane={placementResult.Snapshot.Plane}",
                $"ExchangeStatus={placementResult.Snapshot.Status}",
                $"ExecutedQuantity={FormatDecimal(placementResult.Snapshot.ExecutedQuantity)}",
                $"CumulativeQuoteQuantity={FormatDecimal(placementResult.Snapshot.CumulativeQuoteQuantity)}",
                $"AveragePrice={FormatDecimal(placementResult.Snapshot.AveragePrice)}"
            ]);
    }

    private static bool TryReadBooleanFlag(string? context, string key)
    {
        return bool.TryParse(ReadContextValue(context, key), out var value) && value;
    }

    private static string? ReadContextValue(string? context, string key)
    {
        if (string.IsNullOrWhiteSpace(context))
        {
            return null;
        }

        var prefix = $"{key}=";
        var segments = context.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var segment in segments)
        {
            if (segment.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return segment[prefix.Length..].Trim();
            }
        }

        return null;
    }

    private static bool IsAligned(decimal value, decimal increment)
    {
        if (increment <= 0m)
        {
            return false;
        }

        return value % increment == 0m;
    }

    private static int CountFractionalDigits(decimal value)
    {
        return (decimal.GetBits(value)[3] >> 16) & 0x7F;
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
            BinanceExchangeRejectedException exchangeRejectedException => exchangeRejectedException.FailureCode,
            BinanceClockDriftException => nameof(ExecutionGateBlockedReason.ClockDriftExceeded),
            _ => "DispatchFailed"
        };
    }

    private static string FormatDecimal(decimal value)
    {
        return value.ToString("0.##################", CultureInfo.InvariantCulture);
    }
}

