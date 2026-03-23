namespace CoinBot.Application.Abstractions.Strategies;

public sealed record StrategySignalLogExplainabilitySnapshot(
    string Title,
    string Summary,
    IReadOnlyCollection<string> Drivers,
    IReadOnlyCollection<string> Tags);
