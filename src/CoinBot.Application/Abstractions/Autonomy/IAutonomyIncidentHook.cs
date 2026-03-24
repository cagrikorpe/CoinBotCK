namespace CoinBot.Application.Abstractions.Autonomy;

public interface IAutonomyIncidentHook
{
    Task WriteIncidentAsync(
        AutonomyIncidentHookRequest request,
        CancellationToken cancellationToken = default);

    Task WriteRecoveryAsync(
        AutonomyRecoveryHookRequest request,
        CancellationToken cancellationToken = default);
}
