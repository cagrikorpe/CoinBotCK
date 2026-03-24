using CoinBot.Application.Abstractions.Administration;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;

namespace CoinBot.Infrastructure.Administration;

internal static class GlobalSystemStateDefaults
{
    internal static readonly Guid SingletonId = new("D79D0A6B-E76F-4D3A-A0EE-6D4CC4A9C101");

    internal const string DefaultReasonCode = "SYSTEM_ACTIVE";
    internal const string DefaultSource = "SystemDefault";

    internal static GlobalSystemState CreateEntity()
    {
        return new GlobalSystemState
        {
            Id = SingletonId,
            State = GlobalSystemStateKind.Active,
            ReasonCode = DefaultReasonCode,
            Source = DefaultSource,
            Version = 0
        };
    }

    internal static GlobalSystemStateSnapshot CreateSnapshot()
    {
        return new GlobalSystemStateSnapshot(
            GlobalSystemStateKind.Active,
            DefaultReasonCode,
            Message: null,
            DefaultSource,
            CorrelationId: null,
            IsManualOverride: false,
            ExpiresAtUtc: null,
            UpdatedAtUtc: null,
            UpdatedByUserId: null,
            UpdatedFromIp: null,
            Version: 0,
            IsPersisted: false);
    }
}
