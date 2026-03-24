using CoinBot.Domain.Enums;

namespace CoinBot.Application.Abstractions.Autonomy;

public interface IDependencyCircuitBreakerStateManager
{
    Task<DependencyCircuitBreakerSnapshot> GetSnapshotAsync(
        DependencyCircuitBreakerKind breakerKind,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<DependencyCircuitBreakerSnapshot>> ListSnapshotsAsync(
        CancellationToken cancellationToken = default);

    Task<DependencyCircuitBreakerSnapshot> RecordFailureAsync(
        DependencyCircuitBreakerFailureRequest request,
        CancellationToken cancellationToken = default);

    Task<DependencyCircuitBreakerSnapshot> RecordSuccessAsync(
        DependencyCircuitBreakerSuccessRequest request,
        CancellationToken cancellationToken = default);

    Task<DependencyCircuitBreakerSnapshot?> TryBeginHalfOpenAsync(
        DependencyCircuitBreakerHalfOpenRequest request,
        CancellationToken cancellationToken = default);
}
