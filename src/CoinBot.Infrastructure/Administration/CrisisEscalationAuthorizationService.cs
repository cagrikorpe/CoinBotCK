using CoinBot.Application.Abstractions.Administration;
using Microsoft.Extensions.Logging;

namespace CoinBot.Infrastructure.Administration;

public sealed class CrisisEscalationAuthorizationService(
    ILogger<CrisisEscalationAuthorizationService> logger) : ICrisisEscalationAuthorizationService
{
    public Task ValidateReauthAsync(
        CrisisReauthValidationRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var token = NormalizeRequired(request.Token, nameof(request.Token), 256);
        logger.LogInformation(
            "Crisis re-auth stub accepted level {Level} for scope {Scope}.",
            request.Level,
            request.Scope);

        _ = token;
        return Task.CompletedTask;
    }

    public Task ValidateSecondApprovalAsync(
        CrisisSecondApprovalValidationRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        _ = NormalizeRequired(request.ApprovalReference, nameof(request.ApprovalReference), 128);
        logger.LogInformation(
            "Crisis second approval stub accepted level {Level} for scope {Scope}.",
            request.Level,
            request.Scope);

        return Task.CompletedTask;
    }

    private static string NormalizeRequired(string? value, string parameterName, int maxLength)
    {
        var normalizedValue = value?.Trim();

        if (string.IsNullOrWhiteSpace(normalizedValue))
        {
            throw new InvalidOperationException($"{parameterName} is required.");
        }

        return normalizedValue.Length <= maxLength
            ? normalizedValue
            : throw new ArgumentOutOfRangeException(parameterName, $"The value cannot exceed {maxLength} characters.");
    }
}
