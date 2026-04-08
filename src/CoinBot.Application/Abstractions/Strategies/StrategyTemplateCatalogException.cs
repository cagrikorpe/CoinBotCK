namespace CoinBot.Application.Abstractions.Strategies;

public sealed class StrategyTemplateCatalogException(string failureCode, string message) : InvalidOperationException(message)
{
    public string FailureCode { get; } = failureCode;
}
