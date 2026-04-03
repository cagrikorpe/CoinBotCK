namespace CoinBot.Application.Abstractions.Strategies;

public sealed record StrategyRuleMetadata(
    string? RuleId = null,
    string? RuleType = null,
    string? Timeframe = null,
    decimal Weight = 1m,
    bool Enabled = true,
    string? Group = null)
{
    public static StrategyRuleMetadata Default { get; } = new();
}
