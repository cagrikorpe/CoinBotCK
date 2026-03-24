namespace CoinBot.Application.Abstractions.Execution;

public sealed record UserExecutionOverrideEvaluationResult(
    bool IsBlocked,
    string? BlockCode,
    string? Message);
