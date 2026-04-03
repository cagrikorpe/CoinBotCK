using CoinBot.Application.Abstractions.Risk;

namespace CoinBot.Application.Abstractions.Execution;

public sealed record UserExecutionOverrideEvaluationResult(
    bool IsBlocked,
    string? BlockCode,
    string? Message,
    RiskVetoResult? RiskEvaluation = null);
