namespace CoinBot.Application.Abstractions.Administration;

public interface ICrisisIncidentHook
{
    Task WriteIncidentAsync(
        CrisisIncidentHookRequest request,
        CancellationToken cancellationToken = default);

    Task WriteRecoveryAsync(
        CrisisRecoveryHookRequest request,
        CancellationToken cancellationToken = default);
}
