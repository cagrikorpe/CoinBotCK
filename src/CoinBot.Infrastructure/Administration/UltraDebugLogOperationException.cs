namespace CoinBot.Infrastructure.Administration;

public sealed class UltraDebugLogOperationException(string failureCode, string message) : InvalidOperationException(message)
{
    public string FailureCode { get; } = string.IsNullOrWhiteSpace(failureCode)
        ? "UltraDebugLogOperationFailed"
        : failureCode.Trim();
}
