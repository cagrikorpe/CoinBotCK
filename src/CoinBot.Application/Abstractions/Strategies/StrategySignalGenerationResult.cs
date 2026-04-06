using CoinBot.Application.Abstractions.Ai;

namespace CoinBot.Application.Abstractions.Strategies;

public sealed record StrategySignalGenerationResult(
    StrategyEvaluationResult EvaluationResult,
    IReadOnlyCollection<StrategySignalSnapshot> Signals,
    IReadOnlyCollection<StrategySignalVetoSnapshot> Vetoes,
    int SuppressedDuplicateCount)
{
    public StrategyEvaluationReportSnapshot? EvaluationReport { get; init; }

    public IReadOnlyCollection<AiSignalEvaluationResult> AiEvaluations { get; init; } = Array.Empty<AiSignalEvaluationResult>();
}
