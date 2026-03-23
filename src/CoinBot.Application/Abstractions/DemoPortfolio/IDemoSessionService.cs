namespace CoinBot.Application.Abstractions.DemoPortfolio;

public interface IDemoSessionService
{
    Task<DemoSessionSnapshot?> GetActiveSessionAsync(
        string ownerUserId,
        CancellationToken cancellationToken = default);

    Task<DemoSessionSnapshot> EnsureActiveSessionAsync(
        string ownerUserId,
        CancellationToken cancellationToken = default);

    Task<DemoSessionSnapshot?> RunConsistencyCheckAsync(
        string ownerUserId,
        CancellationToken cancellationToken = default);

    Task<DemoSessionSnapshot> ResetAsync(
        DemoSessionResetRequest request,
        CancellationToken cancellationToken = default);
}
