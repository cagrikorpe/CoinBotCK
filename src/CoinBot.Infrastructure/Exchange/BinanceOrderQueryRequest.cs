namespace CoinBot.Infrastructure.Exchange;

public sealed record BinanceOrderQueryRequest(
    Guid ExchangeAccountId,
    string Symbol,
    string? ExchangeOrderId,
    string? ClientOrderId,
    string ApiKey,
    string ApiSecret,
    string? CommandId = null,
    string? CorrelationId = null,
    string? ExecutionAttemptId = null,
    Guid? ExecutionOrderId = null,
    string? UserId = null)
{
    public override string ToString()
    {
        return $"{nameof(BinanceOrderQueryRequest)} {{ ExchangeAccountId = {ExchangeAccountId}, Symbol = {Symbol}, ExchangeOrderId = {ExchangeOrderId ?? "missing"}, ClientOrderId = {ClientOrderId ?? "missing"}, CommandId = {CommandId ?? "missing"}, CorrelationId = {CorrelationId ?? "missing"}, ExecutionAttemptId = {ExecutionAttemptId ?? "missing"}, ExecutionOrderId = {ExecutionOrderId?.ToString() ?? "missing"}, UserId = {UserId ?? "missing"}, ApiKey = ***REDACTED***, ApiSecret = ***REDACTED*** }}";
    }
}
