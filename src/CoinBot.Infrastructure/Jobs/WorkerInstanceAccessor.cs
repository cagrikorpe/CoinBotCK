using Microsoft.Extensions.Options;

namespace CoinBot.Infrastructure.Jobs;

public sealed class WorkerInstanceAccessor(IOptions<JobOrchestrationOptions> options) : IWorkerInstanceAccessor
{
    public string WorkerInstanceId { get; } = ResolveWorkerInstanceId(options.Value.WorkerInstanceId);

    private static string ResolveWorkerInstanceId(string configuredWorkerInstanceId)
    {
        var normalizedWorkerInstanceId = configuredWorkerInstanceId.Trim();

        return string.IsNullOrWhiteSpace(normalizedWorkerInstanceId)
            ? $"worker-{Guid.NewGuid():N}"
            : normalizedWorkerInstanceId;
    }
}
