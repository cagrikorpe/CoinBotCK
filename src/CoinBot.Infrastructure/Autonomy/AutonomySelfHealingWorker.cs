using CoinBot.Application.Abstractions.Autonomy;
using CoinBot.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CoinBot.Infrastructure.Autonomy;

public sealed class AutonomySelfHealingWorker(
    IServiceScopeFactory serviceScopeFactory,
    IOptions<AutonomyOptions> options,
    ILogger<AutonomySelfHealingWorker> logger) : BackgroundService
{
    private const string SystemActor = "system:autonomy-self-healing";
    private readonly AutonomyOptions optionsValue = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
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
                logger.LogWarning(exception, "Autonomy self-healing worker cycle failed.");
            }

            await Task.Delay(TimeSpan.FromSeconds(optionsValue.SelfHealingIntervalSeconds), stoppingToken);
        }
    }

    internal async Task RunOnceAsync(CancellationToken cancellationToken = default)
    {
        using var scope = serviceScopeFactory.CreateScope();
        var reviewQueueService = scope.ServiceProvider.GetRequiredService<IAutonomyReviewQueueService>();
        var autonomyService = scope.ServiceProvider.GetRequiredService<IAutonomyService>();
        var breakerStateManager = scope.ServiceProvider.GetRequiredService<IDependencyCircuitBreakerStateManager>();

        await reviewQueueService.ExpirePendingAsync(cancellationToken);

        foreach (var breakerKind in Enum.GetValues<DependencyCircuitBreakerKind>())
        {
            var halfOpenSnapshot = await breakerStateManager.TryBeginHalfOpenAsync(
                new DependencyCircuitBreakerHalfOpenRequest(
                    breakerKind,
                    SystemActor,
                    CorrelationId: $"auto-half-open:{breakerKind}:{DateTime.UtcNow:yyyyMMddHHmmss}"),
                cancellationToken);

            if (halfOpenSnapshot is null)
            {
                continue;
            }

            await autonomyService.EvaluateAsync(
                new AutonomyDecisionRequest(
                    ActorUserId: SystemActor,
                    SuggestedAction: ResolveSuggestedAction(breakerKind),
                    Reason: $"Half-open recovery attempt for {breakerKind}.",
                    ConfidenceScore: ResolveConfidenceScore(breakerKind),
                    ScopeKey: $"BREAKER:{breakerKind.ToString().ToUpperInvariant()}",
                    CorrelationId: halfOpenSnapshot.CorrelationId,
                    BreakerKind: breakerKind),
                cancellationToken);
        }
    }

    private static string ResolveSuggestedAction(DependencyCircuitBreakerKind breakerKind)
    {
        return breakerKind switch
        {
            DependencyCircuitBreakerKind.WebSocket => AutonomySuggestedActions.WebSocketReconnect,
            DependencyCircuitBreakerKind.RestMarketData => AutonomySuggestedActions.CacheRebuild,
            DependencyCircuitBreakerKind.OrderExecution => AutonomySuggestedActions.WorkerRetry,
            DependencyCircuitBreakerKind.AccountValidation => AutonomySuggestedActions.WorkerRetry,
            _ => AutonomySuggestedActions.WorkerRetry
        };
    }

    private static decimal ResolveConfidenceScore(DependencyCircuitBreakerKind breakerKind)
    {
        return breakerKind switch
        {
            DependencyCircuitBreakerKind.WebSocket => 0.92m,
            DependencyCircuitBreakerKind.RestMarketData => 0.86m,
            DependencyCircuitBreakerKind.OrderExecution => 0.75m,
            DependencyCircuitBreakerKind.AccountValidation => 0.80m,
            _ => 0.70m
        };
    }
}
