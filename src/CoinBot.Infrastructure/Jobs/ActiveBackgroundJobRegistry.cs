using System.Collections.Concurrent;

namespace CoinBot.Infrastructure.Jobs;

public sealed class ActiveBackgroundJobRegistry
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> activeJobs = new(StringComparer.Ordinal);

    public bool Register(string jobKey, CancellationTokenSource cancellationTokenSource)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobKey);
        ArgumentNullException.ThrowIfNull(cancellationTokenSource);

        return activeJobs.TryAdd(jobKey.Trim(), cancellationTokenSource);
    }

    public IReadOnlyCollection<string> GetJobKeys()
    {
        return activeJobs.Keys
            .OrderBy(jobKey => jobKey, StringComparer.Ordinal)
            .ToArray();
    }

    public void Cancel(string jobKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobKey);

        if (activeJobs.TryGetValue(jobKey.Trim(), out var cancellationTokenSource))
        {
            cancellationTokenSource.Cancel();
        }
    }

    public void Unregister(string jobKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobKey);

        if (activeJobs.TryRemove(jobKey.Trim(), out var cancellationTokenSource))
        {
            cancellationTokenSource.Dispose();
        }
    }
}
