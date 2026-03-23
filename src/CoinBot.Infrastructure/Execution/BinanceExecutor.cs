using CoinBot.Application.Abstractions.Execution;
using CoinBot.Application.Abstractions.ExchangeCredentials;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Exchange;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CoinBot.Infrastructure.Execution;

public sealed class BinanceExecutor(
    ApplicationDbContext dbContext,
    IExchangeCredentialService exchangeCredentialService,
    IBinancePrivateRestClient privateRestClient,
    ILogger<BinanceExecutor> logger) : IExecutionTargetExecutor
{
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

        var credentialAccess = await exchangeCredentialService.GetAsync(
            new ExchangeCredentialAccessRequest(
                exchangeAccountId,
                command.Actor,
                ExchangeCredentialAccessPurpose.Execution,
                order.RootCorrelationId),
            cancellationToken);
        var placementResult = await privateRestClient.PlaceOrderAsync(
            new BinanceOrderPlacementRequest(
                exchangeAccountId,
                command.Symbol,
                command.Side,
                command.OrderType,
                command.Quantity,
                command.Price,
                ExecutionClientOrderId.Create(order.Id),
                credentialAccess.ApiKey,
                credentialAccess.ApiSecret),
            cancellationToken);

        logger.LogInformation(
            "Binance executor submitted order {ExecutionOrderId} for {Symbol}.",
            order.Id,
            command.Symbol);

        return new ExecutionTargetDispatchResult(
            placementResult.OrderId,
            placementResult.SubmittedAtUtc,
            $"ClientOrderId={placementResult.ClientOrderId}");
    }
}
