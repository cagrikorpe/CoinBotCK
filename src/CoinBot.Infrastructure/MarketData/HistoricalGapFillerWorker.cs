using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CoinBot.Infrastructure.MarketData;

public sealed class HistoricalGapFillerWorker(
    IServiceScopeFactory serviceScopeFactory,
    IOptions<HistoricalGapFillerOptions> options,
    ILogger<HistoricalGapFillerWorker> logger) : BackgroundService
{
    private readonly HistoricalGapFillerOptions optionsValue = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!optionsValue.Enabled)
        {
            logger.LogInformation("Historical gap filler worker is disabled by configuration.");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = serviceScopeFactory.CreateScope();
                var gapFillerService = scope.ServiceProvider.GetRequiredService<HistoricalGapFillerService>();
                var summary = await gapFillerService.BackfillAsync(stoppingToken);
                var warmupSummary = await gapFillerService.WarmIndicatorsAsync(stoppingToken);

                logger.LogInformation(
                    "Historical gap filler cycle completed. Symbols={ScannedSymbolCount}, Gaps={DetectedGapCount}, Inserted={InsertedCandleCount}, SkippedDuplicates={SkippedDuplicateCount}, ContinuityVerified={ContinuityVerifiedSymbolCount}, IndicatorPrimed={PrimedSymbolCount}, PrimedCandles={LoadedCandleCount}.",
                    summary.ScannedSymbolCount,
                    summary.DetectedGapCount,
                    summary.InsertedCandleCount,
                    summary.SkippedDuplicateCount,
                    summary.ContinuityVerifiedSymbolCount,
                    warmupSummary.PrimedSymbolCount,
                    warmupSummary.LoadedCandleCount);

                await Task.Delay(TimeSpan.FromMinutes(optionsValue.ScanIntervalMinutes), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception,
                    "Historical gap filler cycle failed. Retrying after {RetryDelaySeconds} seconds.",
                    optionsValue.RetryDelaySeconds);

                await Task.Delay(TimeSpan.FromSeconds(optionsValue.RetryDelaySeconds), stoppingToken);
            }
        }
    }
}
