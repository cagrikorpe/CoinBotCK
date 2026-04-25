using CoinBot.Application.Abstractions.Administration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CoinBot.Infrastructure.Administration;

public sealed class UltraDebugLogRetentionWorker(
    IUltraDebugLogService ultraDebugLogService,
    IOptions<UltraDebugLogRetentionOptions> retentionOptions,
    ILogger<UltraDebugLogRetentionWorker> logger) : BackgroundService
{
    private readonly UltraDebugLogRetentionOptions optionsValue = retentionOptions.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!optionsValue.Enabled)
        {
            logger.LogInformation("Ultra debug log retention worker is disabled by configuration.");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunGuardedOnceAsync(stoppingToken);
                await Task.Delay(TimeSpan.FromMinutes(optionsValue.WorkerIntervalMinutes), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    internal Task RunOnceAsync(CancellationToken cancellationToken = default)
    {
        return ultraDebugLogService.ApplyRetentionAsync(
            new UltraDebugLogRetentionRunRequest(
                BucketName: null,
                DryRun: false,
                MaxFiles: optionsValue.MaxFilesPerRun),
            cancellationToken);
    }

    internal async Task<bool> RunGuardedOnceAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var snapshot = await ultraDebugLogService.ApplyRetentionAsync(
                new UltraDebugLogRetentionRunRequest(
                    BucketName: null,
                    DryRun: false,
                    MaxFiles: optionsValue.MaxFilesPerRun),
                cancellationToken);

            logger.LogInformation(
                "Ultra debug log retention completed. DryRun={DryRun} ScannedFiles={ScannedFiles} DeletedFiles={DeletedFiles} SkippedFiles={SkippedFiles} ReclaimedBytes={ReclaimedBytes} ReasonCode={ReasonCode}.",
                snapshot.DryRun,
                snapshot.ScannedFiles,
                snapshot.DeletedFiles,
                snapshot.SkippedFiles,
                snapshot.ReclaimedBytes,
                snapshot.ReasonCode);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Ultra debug log retention cycle failed. The host will continue.");
            return false;
        }
    }
}
