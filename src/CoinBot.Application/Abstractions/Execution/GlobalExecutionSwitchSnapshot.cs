using CoinBot.Domain.Enums;

namespace CoinBot.Application.Abstractions.Execution;

public sealed record GlobalExecutionSwitchSnapshot(
    TradeMasterSwitchState TradeMasterState,
    bool DemoModeEnabled,
    bool IsPersisted,
    DateTime? LiveModeApprovedAtUtc = null)
{
    public bool IsTradeMasterArmed => TradeMasterState == TradeMasterSwitchState.Armed;

    public bool HasLiveModeApproval => LiveModeApprovedAtUtc.HasValue;

    public ExecutionEnvironment DefaultMode => DemoModeEnabled
        ? ExecutionEnvironment.Demo
        : ExecutionEnvironment.Live;

    public ExecutionEnvironment EffectiveEnvironment => DemoModeEnabled
        ? ExecutionEnvironment.Demo
        : ExecutionEnvironment.Live;
}
