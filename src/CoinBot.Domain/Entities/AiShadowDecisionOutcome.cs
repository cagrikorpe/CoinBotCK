using CoinBot.Domain.Enums;

namespace CoinBot.Domain.Entities;

public sealed class AiShadowDecisionOutcome : UserOwnedEntity
{
    public Guid AiShadowDecisionId { get; set; }
    public Guid BotId { get; set; }
    public string Symbol { get; set; } = string.Empty;
    public string Timeframe { get; set; } = string.Empty;
    public DateTime DecisionEvaluatedAtUtc { get; set; }
    public AiShadowOutcomeHorizonKind HorizonKind { get; set; } = AiShadowOutcomeHorizonKind.BarsForward;
    public int HorizonValue { get; set; }
    public AiShadowOutcomeState OutcomeState { get; set; } = AiShadowOutcomeState.FutureDataUnavailable;
    public decimal? OutcomeScore { get; set; }
    public string RealizedDirectionality { get; set; } = "Unknown";
    public string ConfidenceBucket { get; set; } = "Low";
    public AiShadowFutureDataAvailability FutureDataAvailability { get; set; } = AiShadowFutureDataAvailability.MissingFutureCandle;
    public DateTime? ReferenceCandleCloseTimeUtc { get; set; }
    public DateTime? FutureCandleCloseTimeUtc { get; set; }
    public decimal? ReferenceClosePrice { get; set; }
    public decimal? FutureClosePrice { get; set; }
    public decimal? RealizedReturn { get; set; }
    public bool FalsePositive { get; set; }
    public bool FalseNeutral { get; set; }
    public bool Overtrading { get; set; }
    public bool SuppressionCandidate { get; set; }
    public bool SuppressionAligned { get; set; }
    public DateTime ScoredAtUtc { get; set; }
}
