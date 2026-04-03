namespace CoinBot.Application.Abstractions.Strategies;

public sealed class StrategyDefinitionValidationException(
    string statusCode,
    string message,
    IReadOnlyCollection<string> failureReasons) : Exception(message)
{
    public string StatusCode { get; } = statusCode;

    public IReadOnlyCollection<string> FailureReasons { get; } = failureReasons;
}
