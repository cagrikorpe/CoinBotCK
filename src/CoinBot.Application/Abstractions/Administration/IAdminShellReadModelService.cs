namespace CoinBot.Application.Abstractions.Administration;

public interface IAdminShellReadModelService
{
    Task<AdminShellHealthSnapshot> GetHealthSnapshotAsync(CancellationToken cancellationToken = default);
}
