namespace CoinBot.Domain.Entities;

public sealed class RiskProfile : UserOwnedEntity
{
    public string ProfileName { get; set; } = string.Empty;

    public decimal MaxDailyLossPercentage { get; set; }

    public decimal MaxPositionSizePercentage { get; set; }

    public bool KillSwitchEnabled { get; set; }
}
