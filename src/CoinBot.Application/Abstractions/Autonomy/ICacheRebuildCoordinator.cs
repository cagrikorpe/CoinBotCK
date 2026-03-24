namespace CoinBot.Application.Abstractions.Autonomy;

public interface ICacheRebuildCoordinator
{
    Task<bool> RebuildAsync(
        string? symbol,
        string reason,
        CancellationToken cancellationToken = default);
}
