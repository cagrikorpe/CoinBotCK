using CoinBot.Application.Abstractions.Administration;
using CoinBot.Application.Abstractions.Autonomy;
using CoinBot.Application.Abstractions.ExchangeCredentials;
using CoinBot.Application.Abstractions.MarketData;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.MarketData;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CoinBot.Infrastructure.Autonomy;

public sealed class SelfHealingExecutor(
    IWebSocketReconnectCoordinator webSocketReconnectCoordinator,
    ISignalRReconnectCoordinator signalRReconnectCoordinator,
    IWorkerRetryCoordinator workerRetryCoordinator,
    ICacheRebuildCoordinator cacheRebuildCoordinator,
    IBinanceExchangeInfoClient exchangeInfoClient,
    ISharedSymbolRegistry sharedSymbolRegistry,
    IApiCredentialValidationService apiCredentialValidationService,
    IExchangeCredentialService exchangeCredentialService,
    ApplicationDbContext dbContext,
    ILogger<SelfHealingExecutor> logger) : ISelfHealingExecutor
{
    public async Task<SelfHealingExecutionResult> ExecuteAsync(
        SelfHealingActionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var normalizedAction = NormalizeRequired(request.SuggestedAction, 128, nameof(request.SuggestedAction)).ToUpperInvariant();
        var normalizedReason = NormalizeRequired(request.Reason, 512, nameof(request.Reason));
        var normalizedCorrelationId = NormalizeOptional(request.CorrelationId, 128);

        try
        {
            switch (normalizedAction)
            {
                case AutonomySuggestedActions.WebSocketReconnect:
                    await webSocketReconnectCoordinator.RequestReconnectAsync(normalizedReason, normalizedCorrelationId, cancellationToken);
                    return new SelfHealingExecutionResult(true, "Executed", "WebSocket reconnect requested.");
                case AutonomySuggestedActions.SignalRReconnect:
                    await signalRReconnectCoordinator.RequestReconnectAsync(normalizedReason, normalizedCorrelationId, cancellationToken);
                    return new SelfHealingExecutionResult(true, "Executed", "SignalR reconnect requested.");
                case AutonomySuggestedActions.WorkerRetry:
                    var retriedCount = await workerRetryCoordinator.RetryAsync(request.JobKey, normalizedReason, cancellationToken);
                    return retriedCount > 0
                        ? new SelfHealingExecutionResult(true, "Executed", $"Worker retry armed {retriedCount} job(s).")
                        : new SelfHealingExecutionResult(false, "NoOp", "No retryable worker job matched the request.");
                case AutonomySuggestedActions.CacheRebuild:
                    var rebuildSucceeded = await cacheRebuildCoordinator.RebuildAsync(request.Symbol, normalizedReason, cancellationToken);
                    return rebuildSucceeded
                        ? new SelfHealingExecutionResult(true, "Executed", "Cache rebuild completed.")
                        : new SelfHealingExecutionResult(false, "NoOp", "Cache rebuild did not refresh any data.");
                default:
                    return new SelfHealingExecutionResult(false, "Denied", $"Suggested action '{normalizedAction}' is not allowed for self-healing.");
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                exception,
                "Self-healing execution failed for action {SuggestedAction}.",
                normalizedAction);

            return new SelfHealingExecutionResult(false, "Failed", Truncate(exception.Message, 512));
        }
    }

    public async Task<SelfHealingExecutionResult> ProbeAsync(
        DependencyCircuitBreakerKind breakerKind,
        string actorUserId,
        string? correlationId = null,
        string? jobKey = null,
        string? symbol = null,
        CancellationToken cancellationToken = default)
    {
        _ = actorUserId;
        _ = jobKey;

        try
        {
            switch (breakerKind)
            {
                case DependencyCircuitBreakerKind.WebSocket:
                    return new SelfHealingExecutionResult(true, "Deferred", "WebSocket recovery will be confirmed by the next live message.");
                case DependencyCircuitBreakerKind.RestMarketData:
                    var probeSymbols = !string.IsNullOrWhiteSpace(symbol)
                        ? new[] { symbol.Trim().ToUpperInvariant() }
                        : (await sharedSymbolRegistry.ListSymbolsAsync(cancellationToken))
                            .Select(item => item.Symbol)
                            .Take(1)
                            .ToArray();
                    var resolvedSymbols = probeSymbols.Length == 0 ? new[] { "BTCUSDT" } : probeSymbols;
                    var metadata = await exchangeInfoClient.GetSymbolMetadataAsync(resolvedSymbols, cancellationToken);
                    return metadata.Count > 0
                        ? new SelfHealingExecutionResult(true, "ProbeSucceeded", $"REST market-data probe returned {metadata.Count} symbol(s).")
                        : new SelfHealingExecutionResult(false, "ProbeFailed", "REST market-data probe returned no symbols.");
                case DependencyCircuitBreakerKind.OrderExecution:
                {
                    var exchangeAccountId = await dbContext.ExchangeAccounts
                        .AsNoTracking()
                        .IgnoreQueryFilters()
                        .Where(entity => !entity.IsDeleted && !entity.IsReadOnly && entity.ExchangeName == "Binance")
                        .OrderBy(entity => entity.Id)
                        .Select(entity => (Guid?)entity.Id)
                        .FirstOrDefaultAsync(cancellationToken);

                    if (!exchangeAccountId.HasValue)
                    {
                        return new SelfHealingExecutionResult(true, "ProbeSkipped", "No live Binance account is available for the execution probe.");
                    }

                    var credentialState = await exchangeCredentialService.GetStateAsync(exchangeAccountId.Value, cancellationToken);
                    return credentialState.Status == ExchangeCredentialStatus.Active
                        ? new SelfHealingExecutionResult(true, "ProbeSucceeded", "Order-execution probe confirmed active credential state.")
                        : new SelfHealingExecutionResult(false, "ProbeFailed", $"Order-execution probe found credential state {credentialState.Status}.");
                }
                case DependencyCircuitBreakerKind.AccountValidation:
                    _ = await apiCredentialValidationService.ListAdminSummariesAsync(1, cancellationToken);
                    return new SelfHealingExecutionResult(true, "ProbeSucceeded", $"Account-validation probe completed. CorrelationId={correlationId ?? "none"}.");
                default:
                    return new SelfHealingExecutionResult(false, "ProbeFailed", $"Unsupported breaker kind '{breakerKind}'.");
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                exception,
                "Self-healing dependency probe failed for breaker {BreakerKind}.",
                breakerKind);

            return new SelfHealingExecutionResult(false, "ProbeFailed", Truncate(exception.Message, 512));
        }
    }

    private static string NormalizeRequired(string? value, int maxLength, string parameterName)
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
