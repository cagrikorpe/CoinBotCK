using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CoinBot.Infrastructure.MarketData;

public sealed class MarketScannerWorker(
    IServiceScopeFactory serviceScopeFactory,
    IOptions<MarketScannerOptions> options,
    TimeProvider timeProvider,
    ILogger<MarketScannerWorker> logger) : BackgroundService
{
    private readonly MarketScannerOptions optionsValue = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!optionsValue.Enabled)
        {
            logger.LogInformation("Market scanner worker is disabled by configuration.");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Market scanner worker cycle failed.");
                await RecordFailureAsync(exception, stoppingToken);
            }

            await Task.Delay(TimeSpan.FromSeconds(optionsValue.ScanIntervalSeconds), stoppingToken);
        }
    }

    internal async Task RunOnceAsync(CancellationToken cancellationToken = default)
    {
        using var scope = serviceScopeFactory.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<MarketScannerService>();
        await service.RunOnceAsync(cancellationToken);
    }

    internal async Task RecordFailureAsync(Exception exception, CancellationToken cancellationToken = default)
    {
        try
        {
            using var scope = serviceScopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
            var entity = await dbContext.WorkerHeartbeats
                .SingleOrDefaultAsync(item => item.WorkerKey == MarketScannerService.WorkerKey, cancellationToken);

            if (entity is null)
            {
                entity = new WorkerHeartbeat
                {
                    Id = Guid.NewGuid(),
                    WorkerKey = MarketScannerService.WorkerKey
                };
                dbContext.WorkerHeartbeats.Add(entity);
            }

            entity.WorkerName = MarketScannerService.WorkerName;
            entity.HealthState = MonitoringHealthState.Critical;
            entity.FreshnessTier = MonitoringFreshnessTier.Hot;
            entity.CircuitBreakerState = CircuitBreakerStateCode.Cooldown;
            entity.LastHeartbeatAtUtc = nowUtc;
            entity.LastUpdatedAtUtc = nowUtc;
            entity.ConsecutiveFailureCount += 1;
            entity.LastErrorCode = exception.GetType().Name;
            entity.LastErrorMessage = Truncate(exception.Message, 1024);
            entity.SnapshotAgeSeconds = 0;
            entity.Detail = Truncate(
                $"Worker={MarketScannerService.WorkerName}; Failure={exception.GetType().Name}; Message={exception.Message}",
                2048);

            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception heartbeatException)
        {
            logger.LogWarning(heartbeatException, "Market scanner worker could not persist failure heartbeat.");
        }
    }

    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        return normalized.Length <= maxLength
            ? normalized
            : normalized[..maxLength];
    }
}
