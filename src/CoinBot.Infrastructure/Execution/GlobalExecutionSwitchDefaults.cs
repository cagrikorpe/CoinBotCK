using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;

namespace CoinBot.Infrastructure.Execution;

internal static class GlobalExecutionSwitchDefaults
{
    internal static readonly Guid SingletonId = new("0F4D61F5-595D-4C35-9B21-3D87A0F1D001");

    internal static GlobalExecutionSwitch CreateEntity()
    {
        return new GlobalExecutionSwitch
        {
            Id = SingletonId,
            TradeMasterState = TradeMasterSwitchState.Disarmed,
            DemoModeEnabled = true
        };
    }
}
