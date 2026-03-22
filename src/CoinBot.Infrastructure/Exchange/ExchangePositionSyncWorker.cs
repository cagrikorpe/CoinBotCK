using CoinBot.Application.Abstractions.DataScope;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CoinBot.Infrastructure.Exchange;

public sealed class ExchangePositionSyncWorker(
    IServiceScopeFactory serviceScopeFactory,
    ExchangeAccountSnapshotHub snapshotHub,
    IOptions<BinancePrivateDataOptions> options,
    ILogger<ExchangePositionSyncWorker> logger) : BackgroundService
{
    private readonly BinancePrivateDataOptions optionsValue = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!optionsValue.Enabled)
        {
            logger.LogInformation("Exchange position sync worker is disabled by configuration.");
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
                var positionSyncService = scope.ServiceProvider.GetRequiredService<ExchangePositionSyncService>();
                var syncStateService = scope.ServiceProvider.GetRequiredService<ExchangeAccountSyncStateService>();

                await positionSyncService.ApplyAsync(snapshot, stoppingToken);
                await syncStateService.RecordPositionSyncAsync(snapshot, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                logger.LogWarning(
                    "Exchange position sync worker failed for account {ExchangeAccountId}.",
                    snapshot.ExchangeAccountId);
            }
        }
    }
}
