using CoinBot.Domain.Enums;

namespace CoinBot.Application.Abstractions.Administration;

public interface IApprovalWorkflowService
{
    Task<ApprovalQueueDetailSnapshot> EnqueueAsync(
        ApprovalQueueEnqueueRequest request,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<ApprovalQueueListItem>> ListPendingAsync(
        int take = 100,
        CancellationToken cancellationToken = default);

    Task<ApprovalQueueDetailSnapshot?> GetDetailAsync(
        string approvalReference,
        CancellationToken cancellationToken = default);

    Task<ApprovalQueueDetailSnapshot> ApproveAsync(
        ApprovalQueueDecisionRequest request,
        CancellationToken cancellationToken = default);

    Task<ApprovalQueueDetailSnapshot> RejectAsync(
        ApprovalQueueDecisionRequest request,
        CancellationToken cancellationToken = default);

    Task<ApprovalQueueDetailSnapshot> MarkExecutedAsync(
        string approvalReference,
        string actorUserId,
        string? summary,
        string? correlationId = null,
        CancellationToken cancellationToken = default);

    Task<ApprovalQueueDetailSnapshot> MarkFailedAsync(
        string approvalReference,
        string actorUserId,
        string summary,
        string? correlationId = null,
        CancellationToken cancellationToken = default);

    Task<int> ExpirePendingAsync(CancellationToken cancellationToken = default);
}
