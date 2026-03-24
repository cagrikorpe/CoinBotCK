using CoinBot.Domain.Enums;

namespace CoinBot.Application.Abstractions.Autonomy;

public interface ISelfHealingExecutor
{
    Task<SelfHealingExecutionResult> ExecuteAsync(
        SelfHealingActionRequest request,
        CancellationToken cancellationToken = default);

    Task<SelfHealingExecutionResult> ProbeAsync(
        DependencyCircuitBreakerKind breakerKind,
        string actorUserId,
        string? correlationId = null,
        string? jobKey = null,
        string? symbol = null,
        CancellationToken cancellationToken = default);
}
