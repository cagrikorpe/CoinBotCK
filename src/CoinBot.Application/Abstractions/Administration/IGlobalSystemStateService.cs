namespace CoinBot.Application.Abstractions.Administration;

public interface IGlobalSystemStateService
{
    Task<GlobalSystemStateSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);

    Task<GlobalSystemStateSnapshot> SetStateAsync(
        GlobalSystemStateSetRequest request,
        CancellationToken cancellationToken = default);
}
