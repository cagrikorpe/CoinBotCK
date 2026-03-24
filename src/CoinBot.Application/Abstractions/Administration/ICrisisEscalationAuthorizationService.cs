namespace CoinBot.Application.Abstractions.Administration;

public interface ICrisisEscalationAuthorizationService
{
    Task ValidateReauthAsync(
        CrisisReauthValidationRequest request,
        CancellationToken cancellationToken = default);

    Task ValidateSecondApprovalAsync(
        CrisisSecondApprovalValidationRequest request,
        CancellationToken cancellationToken = default);
}
