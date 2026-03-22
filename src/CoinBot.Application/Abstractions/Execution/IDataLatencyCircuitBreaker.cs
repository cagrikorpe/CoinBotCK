namespace CoinBot.Application.Abstractions.Execution;

public interface IDataLatencyCircuitBreaker
{
    Task<DegradedModeSnapshot> GetSnapshotAsync(
        string? correlationId = null,
        CancellationToken cancellationToken = default);

    Task<DegradedModeSnapshot> RecordHeartbeatAsync(
        DataLatencyHeartbeat heartbeat,
        string? correlationId = null,
        CancellationToken cancellationToken = default);
}