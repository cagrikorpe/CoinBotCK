using CoinBot.Domain.Enums;

namespace CoinBot.Domain.Entities;

public sealed class TradingStrategy : UserOwnedEntity
{
    public string StrategyKey { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public bool UsesExplicitVersionLifecycle { get; set; }

    public Guid? ActiveTradingStrategyVersionId { get; set; }

    public DateTime? ActiveVersionActivatedAtUtc { get; set; }

    public StrategyPromotionState PromotionState { get; set; } = StrategyPromotionState.Draft;

    public ExecutionEnvironment? PublishedMode { get; set; }

    public DateTime? PublishedAtUtc { get; set; }

    public DateTime? LivePromotionApprovedAtUtc { get; set; }

    public string? LivePromotionApprovalReference { get; set; }
}
