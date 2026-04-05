using CoinBot.Application.Abstractions.DataScope;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CoinBot.Infrastructure.Exchange;

public sealed class SpotExchangeAppStateSyncWorker(
    IServiceScopeFactory serviceScopeFactory,
    IOptions<BinancePrivateDataOptions> options,
    ILogger<SpotExchangeAppStateSyncWorker> logger) : BackgroundService
{
    private readonly BinancePrivateDataOptions optionsValue = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "Spot exchange app-state sync worker starting. Enabled={Enabled}.",
            optionsValue.Enabled);

        if (!optionsValue.Enabled)
        {
            logger.LogInformation("Spot exchange-app state sync worker is disabled by configuration.");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = serviceScopeFactory.CreateScope();
                using var systemScope = scope.ServiceProvider
                    .GetRequiredService<IDataScopeContextAccessor>()
                    .BeginScope(hasIsolationBypass: true);
                var stateSyncService = scope.ServiceProvider.GetRequiredService<SpotExchangeAppStateSyncService>();

                await stateSyncService.RunOnceAsync(stoppingToken);
                await Task.Delay(TimeSpan.FromMinutes(optionsValue.ReconciliationIntervalMinutes), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                logger.LogWarning("Spot exchange-app state sync worker cycle failed.");
                await Task.Delay(TimeSpan.FromSeconds(optionsValue.ReconnectDelaySeconds), stoppingToken);
            }
        }
    }
}
