using CoinBot.Domain.Entities;

namespace CoinBot.Infrastructure.Features;

public interface IMlShadowScoringService
{
    MlShadowScoreSnapshot Evaluate(MlShadowScoreInput input);
}

public sealed record MlShadowScoreInput(
    TradingFeatureSnapshot? FeatureSnapshot,
    decimal CombinedBaselineScore,
    decimal FeatureCompletenessScore,
    decimal RiskPenalty,
    string GuardDecision,
    string ExecutionDecision,
    string FeatureSchemaVersion);

public sealed record MlShadowScoreSnapshot(
    decimal? MlShadowScore,
    decimal MlConfidence,
    string MlShadowDecision,
    string ModelVersion,
    string FeatureSchemaVersion,
    string ReasonSummary,
    bool IsDecisionInfluential);
