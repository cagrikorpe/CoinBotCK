using CoinBot.Infrastructure.Jobs;
using Microsoft.Extensions.Options;

namespace CoinBot.Worker;

public sealed class Worker(
    IServiceScopeFactory serviceScopeFactory,
    IOptions<JobOrchestrationOptions> options,
    ILogger<Worker> logger) : BackgroundService
{
    private readonly JobOrchestrationOptions optionsValue = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!optionsValue.Enabled)
        {
            logger.LogInformation("Job scheduler worker is disabled by configuration.");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = serviceScopeFactory.CreateScope();
                var scheduler = scope.ServiceProvider.GetRequiredService<BotJobSchedulerService>();
                var triggeredCount = await scheduler.RunDueJobsAsync(stoppingToken);

                logger.LogInformation(
                    "Job scheduler cycle completed. TriggeredJobs={TriggeredJobs}.",
                    triggeredCount);

                await Task.Delay(TimeSpan.FromSeconds(optionsValue.SchedulerPollIntervalSeconds), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception,
                    "Job scheduler cycle failed. Backing off for {InitialRetryDelaySeconds} seconds.",
                    optionsValue.InitialRetryDelaySeconds);

                await Task.Delay(TimeSpan.FromSeconds(optionsValue.InitialRetryDelaySeconds), stoppingToken);
            }
        }
    }
}
