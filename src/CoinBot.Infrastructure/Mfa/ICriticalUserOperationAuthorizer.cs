namespace CoinBot.Infrastructure.Mfa;

public interface ICriticalUserOperationAuthorizer
{
    Task<CriticalUserOperationAuthorizationResult> AuthorizeAsync(
        CriticalUserOperationAuthorizationRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record CriticalUserOperationAuthorizationRequest(
    string UserId,
    string Actor,
    string OperationKey,
    string Target,
    string? CorrelationId = null);

public sealed record CriticalUserOperationAuthorizationResult(
    bool IsAuthorized,
    string? FailureCode,
    string? FailureReason);
