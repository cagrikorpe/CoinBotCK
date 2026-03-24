namespace CoinBot.Application.Abstractions.Administration;

public interface IApiCredentialValidationService
{
    Task UpsertStoredCredentialAsync(
        ApiCredentialStoreMirrorRequest request,
        CancellationToken cancellationToken = default);

    Task<ApiCredentialValidationSnapshot> RecordValidationAsync(
        ApiCredentialValidationRequest request,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<ApiCredentialAdminSummary>> ListAdminSummariesAsync(
        int take = 100,
        CancellationToken cancellationToken = default);
}
