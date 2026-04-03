namespace CoinBot.Application.Abstractions.Strategies;

public sealed record StrategyDefinitionValidationSnapshot(
    bool IsValid,
    string StatusCode,
    string Summary,
    IReadOnlyCollection<string> FailureReasons,
    int EnabledRuleCount);
