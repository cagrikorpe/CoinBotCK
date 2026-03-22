using CoinBot.Application.Abstractions.DataScope;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CoinBot.Infrastructure.Exchange;

public sealed class ExchangeBalanceSyncWorker(
    IServiceScopeFactory serviceScopeFactory,
    ExchangeAccountSnapshotHub snapshotHub,
    IOptions<BinancePrivateDataOptions> options,
    ILogger<ExchangeBalanceSyncWorker> logger) : BackgroundService
{
    private readonly BinancePrivateDataOptions optionsValue = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!optionsValue.Enabled)
        {
            logger.LogInformation("Exchange balance sync worker is disabled by configuration.");
            return;
        }

        await foreach (var snapshot in snapshotHub.SubscribeAsync(stoppingToken))
        {
            try
            {
                using var scope = serviceScopeFactory.CreateScope();
                using var systemScope = scope.ServiceProvider
                    .GetRequiredService<IDataScopeContextAccessor>()
                    .BeginScope(hasIsolationBypass: true);
                var balanceSyncService = scope.ServiceProvider.GetRequiredService<ExchangeBalanceSyncService>();
                var syncStateService = scope.ServiceProvider.GetRequiredService<ExchangeAccountSyncStateService>();

                await balanceSyncService.ApplyAsync(snapshot, stoppingToken);
                await syncStateService.RecordBalanceSyncAsync(snapshot, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                logger.LogWarning(
                    "Exchange balance sync worker failed for account {ExchangeAccountId}.",
                    snapshot.ExchangeAccountId);
            }
        }
    }
}
