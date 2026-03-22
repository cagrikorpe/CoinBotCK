using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;

namespace CoinBot.Infrastructure.Execution;

public static class DegradedModeDefaults
{
    public static readonly Guid SingletonId = Guid.Parse("3e17e8ef-3a73-45cc-8c32-a11fa55178d7");

    public static DegradedModeState CreateEntity(DateTime createdAtUtc)
    {
        return new DegradedModeState
        {
            Id = SingletonId,
            StateCode = DegradedModeStateCode.Stopped,
            ReasonCode = DegradedModeReasonCode.MarketDataUnavailable,
            SignalFlowBlocked = true,
            ExecutionFlowBlocked = true,
            LastStateChangedAtUtc = createdAtUtc
        };
    }
}