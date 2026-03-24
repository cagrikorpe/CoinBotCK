using CoinBot.Application.Abstractions.Administration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CoinBot.Infrastructure.Administration;

public sealed class LogCenterRetentionWorker(
    IServiceScopeFactory serviceScopeFactory,
    IOptions<LogCenterRetentionOptions> retentionOptions,
    ILogger<LogCenterRetentionWorker> logger) : BackgroundService
{
    private readonly LogCenterRetentionOptions optionsValue = retentionOptions.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!optionsValue.Enabled)
        {
            logger.LogInformation("Log center retention worker is disabled by configuration.");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
                await Task.Delay(TimeSpan.FromMinutes(optionsValue.WorkerIntervalMinutes), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception,
                    "Log center retention cycle failed. Backing off for 5 minutes.");

                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
            }
        }
    }

    internal async Task RunOnceAsync(CancellationToken cancellationToken = default)
    {
        using var scope = serviceScopeFactory.CreateScope();
        var retentionService = scope.ServiceProvider.GetRequiredService<ILogCenterRetentionService>();
        await retentionService.ApplyAsync(cancellationToken);
    }
}
