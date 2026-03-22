using CoinBot.Domain.Enums;

namespace CoinBot.Application.Abstractions.Execution;

public interface IGlobalExecutionSwitchService
{
    Task<GlobalExecutionSwitchSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);

    Task<GlobalExecutionSwitchSnapshot> SetTradeMasterStateAsync(
        TradeMasterSwitchState tradeMasterState,
        string actor,
        string? context = null,
        string? correlationId = null,
        CancellationToken cancellationToken = default);

    Task<GlobalExecutionSwitchSnapshot> SetDemoModeAsync(
        bool isEnabled,
        string actor,
        TradingModeLiveApproval? liveApproval = null,
        string? context = null,
        string? correlationId = null,
        CancellationToken cancellationToken = default);
}
