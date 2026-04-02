using CoinBot.Application.Abstractions.DataScope;
using CoinBot.Infrastructure.Alerts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CoinBot.Infrastructure.Exchange;

public sealed class ExchangeBalanceSyncWorker(
    IServiceScopeFactory serviceScopeFactory,
    ExchangeAccountSnapshotHub snapshotHub,
    IOptions<BinancePrivateDataOptions> options,
    ILogger<ExchangeBalanceSyncWorker> logger,
    IAlertDispatchCoordinator? alertDispatchCoordinator = null,
    IHostEnvironment? hostEnvironment = null) : BackgroundService
{
    private readonly BinancePrivateDataOptions optionsValue = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "Exchange balance sync worker starting. Enabled={Enabled}.",
            optionsValue.Enabled);

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

                if (alertDispatchCoordinator is not null)
                {
                    await alertDispatchCoordinator.SendAsync(
                        new CoinBot.Application.Abstractions.Alerts.AlertNotification(
                            Code: "SYNC_FAILED_BALANCE",
                            Severity: CoinBot.Application.Abstractions.Alerts.AlertSeverity.Warning,
                            Title: "SyncFailed",
                            Message:
                                $"EventType=SyncFailed; SyncKind=Balance; ExchangeAccountId={snapshot.ExchangeAccountId:N}; Symbol=BTCUSDT; Result=Failed; FailureCode=BalanceSyncFailed; TimestampUtc={DateTime.UtcNow:O}; Environment={ResolveEnvironmentLabel(hostEnvironment)}",
                            CorrelationId: null),
                        $"sync-failed:balance:{snapshot.ExchangeAccountId:N}",
                        TimeSpan.FromMinutes(5),
                        stoppingToken);
                }
            }
        }
    }

    private static string ResolveEnvironmentLabel(IHostEnvironment? hostEnvironment)
    {
        var runtimeLabel = hostEnvironment?.EnvironmentName ?? "Unknown";
        var planeLabel = hostEnvironment?.IsDevelopment() == true
            ? "Testnet"
            : "Live";

        return $"{runtimeLabel}/{planeLabel}";
    }
}
