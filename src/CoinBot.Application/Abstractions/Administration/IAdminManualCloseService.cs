using CoinBot.Application.Abstractions.Execution;

namespace CoinBot.Application.Abstractions.Administration;

public interface IAdminManualCloseService
{
    Task<AdminManualCloseResult> CloseAsync(
        AdminManualCloseRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record AdminManualCloseRequest(
    Guid BotId,
    string ActorUserId,
    string ExecutionActor,
    string? CorrelationId);

public sealed record AdminManualCloseResult(
    bool IsSuccess,
    string OutcomeCode,
    string Summary,
    string UserMessage,
    ExecutionOrderSnapshot? Order = null,
    bool IsDuplicate = false);
