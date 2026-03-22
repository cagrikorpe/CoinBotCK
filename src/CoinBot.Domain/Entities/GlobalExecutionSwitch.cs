using CoinBot.Domain.Enums;

namespace CoinBot.Domain.Entities;

public sealed class GlobalExecutionSwitch : BaseEntity
{
    public TradeMasterSwitchState TradeMasterState { get; set; } = TradeMasterSwitchState.Disarmed;

    public bool DemoModeEnabled { get; set; } = true;

    public DateTime? LiveModeApprovedAtUtc { get; set; }

    public string? LiveModeApprovalReference { get; set; }
}
