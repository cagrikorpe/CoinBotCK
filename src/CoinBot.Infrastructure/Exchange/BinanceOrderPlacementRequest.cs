using CoinBot.Domain.Enums;

namespace CoinBot.Infrastructure.Exchange;

public sealed record BinanceOrderPlacementRequest(
    Guid ExchangeAccountId,
    string Symbol,
    ExecutionOrderSide Side,
    ExecutionOrderType OrderType,
    decimal Quantity,
    decimal Price,
    string ClientOrderId,
    string ApiKey,
    string ApiSecret,
    string? CommandId = null,
    string? CorrelationId = null,
    string? ExecutionAttemptId = null,
    Guid? ExecutionOrderId = null,
    string? UserId = null,
    bool ReduceOnly = false,
    decimal? QuoteOrderQuantity = null,
    string? TimeInForce = null)
{
    public override string ToString()
    {
        return $"{nameof(BinanceOrderPlacementRequest)} {{ ExchangeAccountId = {ExchangeAccountId}, Symbol = {Symbol}, Side = {Side}, OrderType = {OrderType}, Quantity = {Quantity}, Price = {Price}, ClientOrderId = {ClientOrderId}, CommandId = {CommandId ?? "missing"}, CorrelationId = {CorrelationId ?? "missing"}, ExecutionAttemptId = {ExecutionAttemptId ?? "missing"}, ExecutionOrderId = {ExecutionOrderId?.ToString() ?? "missing"}, UserId = {UserId ?? "missing"}, ReduceOnly = {ReduceOnly}, QuoteOrderQuantity = {QuoteOrderQuantity?.ToString() ?? "missing"}, TimeInForce = {TimeInForce ?? "missing"}, ApiKey = ***REDACTED***, ApiSecret = ***REDACTED*** }}";
    }
}
