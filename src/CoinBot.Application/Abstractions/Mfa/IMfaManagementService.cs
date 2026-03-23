namespace CoinBot.Application.Abstractions.Mfa;

public interface IMfaManagementService
{
    Task<MfaStatusSnapshot> GetStatusAsync(string userId, CancellationToken cancellationToken = default);

    Task<MfaAuthenticatorSetupSnapshot?> GetAuthenticatorSetupAsync(
        string userId,
        bool createIfMissing = false,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>?> EnableAuthenticatorAsync(
        string userId,
        string code,
        CancellationToken cancellationToken = default);

    Task<bool> DisableAsync(
        string userId,
        string verificationCode,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>?> RegenerateRecoveryCodesAsync(
        string userId,
        string verificationCode,
        CancellationToken cancellationToken = default);

    Task<bool> VerifyAsync(
        string userId,
        string provider,
        string code,
        string? purpose = null,
        CancellationToken cancellationToken = default);

    Task<bool> TryRedeemRecoveryCodeAsync(
        string userId,
        string recoveryCode,
        CancellationToken cancellationToken = default);
}
