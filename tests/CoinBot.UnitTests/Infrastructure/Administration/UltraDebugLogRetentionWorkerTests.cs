using CoinBot.Application.Abstractions.Administration;
using CoinBot.Infrastructure.Administration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CoinBot.UnitTests.Infrastructure.Administration;

public sealed class UltraDebugLogRetentionWorkerTests
{
    [Fact]
    public async Task ScheduledJanitor_InvokesRetentionPolicy_Bounded()
    {
        var service = new FakeUltraDebugLogService();
        var worker = new UltraDebugLogRetentionWorker(
            service,
            Options.Create(new UltraDebugLogRetentionOptions
            {
                Enabled = true,
                MaxFilesPerRun = 25,
                WorkerIntervalMinutes = 60
            }),
            NullLogger<UltraDebugLogRetentionWorker>.Instance);

        await worker.RunOnceAsync();

        Assert.NotNull(service.RetentionRequest);
        Assert.False(service.RetentionRequest!.DryRun);
        Assert.Equal(25, service.RetentionRequest.MaxFiles);
        Assert.Null(service.RetentionRequest.BucketName);
    }

    [Fact]
    public async Task ScheduledJanitor_FailureDoesNotCrashHost()
    {
        var service = new FakeUltraDebugLogService
        {
            RetentionException = new IOException("janitor failed")
        };
        var worker = new UltraDebugLogRetentionWorker(
            service,
            Options.Create(new UltraDebugLogRetentionOptions
            {
                Enabled = true,
                MaxFilesPerRun = 25,
                WorkerIntervalMinutes = 60
            }),
            NullLogger<UltraDebugLogRetentionWorker>.Instance);

        var completed = await worker.RunGuardedOnceAsync();

        Assert.False(completed);
    }

    private sealed class FakeUltraDebugLogService : IUltraDebugLogService
    {
        public UltraDebugLogRetentionRunRequest? RetentionRequest { get; private set; }

        public Exception? RetentionException { get; set; }

        public IReadOnlyCollection<UltraDebugLogDurationOption> GetDurationOptions() => Array.Empty<UltraDebugLogDurationOption>();

        public IReadOnlyCollection<UltraDebugLogSizeLimitOption> GetLogSizeLimitOptions() => Array.Empty<UltraDebugLogSizeLimitOption>();

        public Task<UltraDebugLogSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new UltraDebugLogSnapshot(false, null, null, null, null, null, null, null, false));

        public Task<UltraDebugLogSnapshot> EnableAsync(UltraDebugLogEnableRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<UltraDebugLogSnapshot> DisableAsync(UltraDebugLogDisableRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<UltraDebugLogTailSnapshot> SearchAsync(UltraDebugLogSearchRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<UltraDebugLogExportSnapshot> ExportAsync(UltraDebugLogExportRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<UltraDebugLogRetentionRunSnapshot> ApplyRetentionAsync(
            UltraDebugLogRetentionRunRequest request,
            CancellationToken cancellationToken = default)
        {
            RetentionRequest = request;
            if (RetentionException is not null)
            {
                throw RetentionException;
            }

            return Task.FromResult(new UltraDebugLogRetentionRunSnapshot(
                StartedAtUtc: new DateTime(2026, 4, 25, 8, 0, 0, DateTimeKind.Utc),
                CompletedAtUtc: new DateTime(2026, 4, 25, 8, 1, 0, DateTimeKind.Utc),
                DryRun: request.DryRun,
                ScannedFiles: request.MaxFiles ?? 0,
                DeletedFiles: 0,
                SkippedFiles: 0,
                ReclaimedBytes: 0,
                CandidateDeleteFiles: 0,
                CandidateReclaimedBytes: 0,
                ReasonCode: "Completed",
                Buckets: Array.Empty<UltraDebugLogRetentionBucketSnapshot>()));
        }

        public Task WriteAsync(UltraDebugLogEntry entry, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
