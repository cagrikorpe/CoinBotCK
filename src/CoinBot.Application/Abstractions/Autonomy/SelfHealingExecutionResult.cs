namespace CoinBot.Application.Abstractions.Autonomy;

public sealed record SelfHealingExecutionResult(
    bool IsExecuted,
    string Outcome,
    string? Detail);
