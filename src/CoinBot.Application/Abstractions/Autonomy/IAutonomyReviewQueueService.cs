using CoinBot.Domain.Enums;

namespace CoinBot.Application.Abstractions.Autonomy;

public interface IAutonomyReviewQueueService
{
    Task<AutonomyReviewQueueItem> EnqueueAsync(
        AutonomyReviewQueueEnqueueRequest request,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<AutonomyReviewQueueItem>> ListAsync(
        AutonomyReviewStatus? status = null,
        CancellationToken cancellationToken = default);

    Task<int> ExpirePendingAsync(CancellationToken cancellationToken = default);
}
