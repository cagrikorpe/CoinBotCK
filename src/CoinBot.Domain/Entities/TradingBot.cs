using CoinBot.Domain.Enums;

namespace CoinBot.Domain.Entities;

public sealed class TradingBot : UserOwnedEntity
{
    public string Name { get; set; } = string.Empty;

    public string StrategyKey { get; set; } = string.Empty;

    public string? Symbol { get; set; }

    public decimal? Quantity { get; set; }

    public Guid? ExchangeAccountId { get; set; }

    public decimal? Leverage { get; set; }

    public string? MarginType { get; set; }

    public bool IsEnabled { get; set; }

    public ExecutionEnvironment? TradingModeOverride { get; set; }

    public DateTime? TradingModeApprovedAtUtc { get; set; }

    public string? TradingModeApprovalReference { get; set; }

    public int OpenOrderCount { get; set; }

    public int OpenPositionCount { get; set; }
}
