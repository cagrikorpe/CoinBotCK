using CoinBot.Infrastructure.Jobs;
using Microsoft.Extensions.Options;

namespace CoinBot.Worker;

public sealed class JobCleanupWorker(
    IServiceScopeFactory serviceScopeFactory,
    IOptions<JobOrchestrationOptions> options,
    ILogger<JobCleanupWorker> logger) : BackgroundService
{
    private readonly JobOrchestrationOptions optionsValue = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!optionsValue.Enabled)
        {
            logger.LogInformation("Job cleanup worker is disabled by configuration.");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = serviceScopeFactory.CreateScope();
                var cleanupService = scope.ServiceProvider.GetRequiredService<BackgroundJobCleanupService>();
                await cleanupService.RunAsync(stoppingToken);
                await Task.Delay(TimeSpan.FromSeconds(optionsValue.CleanupIntervalSeconds), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception,
                    "Job cleanup cycle failed. Backing off for {InitialRetryDelaySeconds} seconds.",
                    optionsValue.InitialRetryDelaySeconds);

                await Task.Delay(TimeSpan.FromSeconds(optionsValue.InitialRetryDelaySeconds), stoppingToken);
            }
        }
    }
}
