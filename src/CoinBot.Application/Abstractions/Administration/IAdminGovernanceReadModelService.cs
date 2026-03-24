namespace CoinBot.Application.Abstractions.Administration;

public interface IAdminGovernanceReadModelService
{
    Task<IReadOnlyCollection<IncidentListItem>> ListIncidentsAsync(
        int take = 100,
        CancellationToken cancellationToken = default);

    Task<IncidentDetailSnapshot?> GetIncidentDetailAsync(
        string incidentReference,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<SystemStateHistoryListItem>> ListSystemStateHistoryAsync(
        int take = 100,
        CancellationToken cancellationToken = default);

    Task<SystemStateHistoryDetailSnapshot?> GetSystemStateHistoryDetailAsync(
        string historyReference,
        CancellationToken cancellationToken = default);
}
