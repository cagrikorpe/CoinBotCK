using CoinBot.Application.Abstractions.DataScope;
using CoinBot.Infrastructure.Exchange;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CoinBot.Infrastructure.Execution;

public sealed class ExecutionReconciliationWorker(
    IServiceScopeFactory serviceScopeFactory,
    IOptions<BinancePrivateDataOptions> options,
    ILogger<ExecutionReconciliationWorker> logger) : BackgroundService
{
    private readonly BinancePrivateDataOptions optionsValue = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!optionsValue.Enabled)
        {
            logger.LogInformation("Execution reconciliation worker is disabled by configuration.");
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
                var reconciliationService = scope.ServiceProvider.GetRequiredService<ExecutionReconciliationService>();

                await reconciliationService.RunOnceAsync(stoppingToken);
                await Task.Delay(TimeSpan.FromMinutes(optionsValue.ReconciliationIntervalMinutes), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                logger.LogWarning("Execution reconciliation worker cycle failed.");
                await Task.Delay(TimeSpan.FromSeconds(optionsValue.ReconnectDelaySeconds), stoppingToken);
            }
        }
    }
}
