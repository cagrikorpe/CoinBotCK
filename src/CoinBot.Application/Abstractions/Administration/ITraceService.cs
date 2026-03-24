namespace CoinBot.Application.Abstractions.Administration;

public interface ITraceService
{
    Task<DecisionTraceSnapshot> WriteDecisionTraceAsync(
        DecisionTraceWriteRequest request,
        CancellationToken cancellationToken = default);

    Task<ExecutionTraceSnapshot> WriteExecutionTraceAsync(
        ExecutionTraceWriteRequest request,
        CancellationToken cancellationToken = default);

    Task<DecisionTraceSnapshot?> GetDecisionTraceByStrategySignalIdAsync(
        Guid strategySignalId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<AdminTraceListItem>> SearchAsync(
        AdminTraceSearchRequest request,
        CancellationToken cancellationToken = default);

    Task<AdminTraceDetailSnapshot?> GetDetailAsync(
        string correlationId,
        string? decisionId = null,
        string? executionAttemptId = null,
        CancellationToken cancellationToken = default);
}
