using CoinBot.Infrastructure.Jobs;
using Microsoft.Extensions.Options;

namespace CoinBot.Worker;

public sealed class JobKeepAliveWorker(
    IServiceScopeFactory serviceScopeFactory,
    IOptions<JobOrchestrationOptions> options,
    ILogger<JobKeepAliveWorker> logger) : BackgroundService
{
    private readonly JobOrchestrationOptions optionsValue = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!optionsValue.Enabled)
        {
            logger.LogInformation("Job keepalive worker is disabled by configuration.");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = serviceScopeFactory.CreateScope();
                var keepAliveService = scope.ServiceProvider.GetRequiredService<BackgroundJobKeepAliveService>();
                await keepAliveService.RunAsync(stoppingToken);
                await Task.Delay(TimeSpan.FromSeconds(optionsValue.KeepAliveIntervalSeconds), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception,
                    "Job keepalive cycle failed. Backing off for {InitialRetryDelaySeconds} seconds.",
                    optionsValue.InitialRetryDelaySeconds);

                await Task.Delay(TimeSpan.FromSeconds(optionsValue.InitialRetryDelaySeconds), stoppingToken);
            }
        }
    }
}
