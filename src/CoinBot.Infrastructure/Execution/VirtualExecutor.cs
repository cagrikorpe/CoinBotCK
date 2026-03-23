using CoinBot.Application.Abstractions.Execution;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace CoinBot.Infrastructure.Execution;

public sealed class VirtualExecutor(
    TimeProvider timeProvider,
    ILogger<VirtualExecutor> logger) : IExecutionTargetExecutor
{
    public ExecutionOrderExecutorKind Kind => ExecutionOrderExecutorKind.Virtual;

    public Task<ExecutionTargetDispatchResult> DispatchAsync(
        ExecutionOrder order,
        ExecutionCommand command,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var submittedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
        var externalOrderId = $"virtual:{order.Id:N}";

        logger.LogInformation(
            "Virtual executor accepted order {ExecutionOrderId} for {Symbol}.",
            order.Id,
            command.Symbol);

        return Task.FromResult(
            new ExecutionTargetDispatchResult(
                externalOrderId,
                submittedAtUtc,
                "AcceptedByVirtualExecutor"));
    }
}
