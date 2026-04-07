namespace CoinBot.Infrastructure.Exchange;

public sealed class BinanceExchangeRejectedException(
    string failureCode,
    string message,
    string? exchangeCode = null,
    int? httpStatusCode = null) : InvalidOperationException(message)
{
    public string FailureCode { get; } = string.IsNullOrWhiteSpace(failureCode)
        ? "ExchangeRejected"
        : failureCode;

    public string? ExchangeCode { get; } = string.IsNullOrWhiteSpace(exchangeCode)
        ? null
        : exchangeCode.Trim();

    public int? HttpStatusCode { get; } = httpStatusCode;
}

