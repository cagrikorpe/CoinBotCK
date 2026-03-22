using CoinBot.Infrastructure.Jobs;
using Microsoft.Extensions.Options;

namespace CoinBot.Worker;

public sealed class JobWatchdogWorker(
    IServiceScopeFactory serviceScopeFactory,
    IOptions<JobOrchestrationOptions> options,
    ILogger<JobWatchdogWorker> logger) : BackgroundService
{
    private readonly JobOrchestrationOptions optionsValue = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!optionsValue.Enabled)
        {
            logger.LogInformation("Job watchdog worker is disabled by configuration.");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = serviceScopeFactory.CreateScope();
                var watchdogService = scope.ServiceProvider.GetRequiredService<BackgroundJobWatchdogService>();
                await watchdogService.RunAsync(stoppingToken);
                await Task.Delay(TimeSpan.FromSeconds(optionsValue.WatchdogIntervalSeconds), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception,
                    "Job watchdog cycle failed. Backing off for {InitialRetryDelaySeconds} seconds.",
                    optionsValue.InitialRetryDelaySeconds);

                await Task.Delay(TimeSpan.FromSeconds(optionsValue.InitialRetryDelaySeconds), stoppingToken);
            }
        }
    }
}
