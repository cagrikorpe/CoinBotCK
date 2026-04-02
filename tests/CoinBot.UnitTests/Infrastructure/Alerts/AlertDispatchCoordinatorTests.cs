using CoinBot.Application.Abstractions.Alerts;
using CoinBot.Infrastructure.Alerts;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace CoinBot.UnitTests.Infrastructure.Alerts;

public sealed class AlertDispatchCoordinatorTests
{
    [Fact]
    public async Task SendAsync_DispatchesThroughScopedAlertService()
    {
        var alertService = new RecordingAlertService();
        await using var provider = new ServiceCollection()
            .AddScoped<IAlertService>(_ => alertService)
            .BuildServiceProvider();
        var coordinator = new AlertDispatchCoordinator(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<AlertDispatchCoordinator>.Instance);

        await coordinator.SendAsync(
            new AlertNotification(
                "ORDER_SUBMITTED",
                AlertSeverity.Information,
                "OrderSubmitted",
                "EventType=OrderSubmitted; Symbol=BTCUSDT; Result=Submitted; TimestampUtc=2026-04-02T10:00:00.0000000Z; Environment=Development/Testnet"),
            "order-transition:test-1",
            TimeSpan.FromMinutes(5));

        var notification = Assert.Single(alertService.Notifications);
        Assert.Equal("ORDER_SUBMITTED", notification.Code);
        Assert.Contains("EventType=OrderSubmitted", notification.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendAsync_BlocksDuplicateDispatchesWithinCooldown()
    {
        var alertService = new RecordingAlertService();
        await using var provider = new ServiceCollection()
            .AddScoped<IAlertService>(_ => alertService)
            .BuildServiceProvider();
        var coordinator = new AlertDispatchCoordinator(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<AlertDispatchCoordinator>.Instance);
        var notification = new AlertNotification(
            "ORDER_REJECTED",
            AlertSeverity.Warning,
            "OrderRejected",
            "EventType=OrderRejected; Symbol=BTCUSDT; Result=Rejected; FailureCode=ExecutionValidationException; TimestampUtc=2026-04-02T10:00:00.0000000Z; Environment=Development/Testnet");

        await coordinator.SendAsync(notification, "order-transition:test-2", TimeSpan.FromMinutes(5));
        await coordinator.SendAsync(notification, "order-transition:test-2", TimeSpan.FromMinutes(5));

        Assert.Single(alertService.Notifications);
    }

    [Fact]
    public async Task SendAsync_SwallowsAlertServiceFailures()
    {
        await using var provider = new ServiceCollection()
            .AddScoped<IAlertService, ThrowingAlertService>()
            .BuildServiceProvider();
        var coordinator = new AlertDispatchCoordinator(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<AlertDispatchCoordinator>.Instance);

        var exception = await Record.ExceptionAsync(() =>
            coordinator.SendAsync(
                new AlertNotification(
                    "SYNC_FAILED_BALANCE",
                    AlertSeverity.Warning,
                    "SyncFailed",
                    "EventType=SyncFailed; SyncKind=Balance; Result=Failed; TimestampUtc=2026-04-02T10:00:00.0000000Z; Environment=Development/Testnet"),
                "sync-failed:test-3",
                TimeSpan.FromMinutes(5)));

        Assert.Null(exception);
    }

    private sealed class RecordingAlertService : IAlertService
    {
        public List<AlertNotification> Notifications { get; } = [];

        public Task SendAsync(AlertNotification notification, CancellationToken cancellationToken = default)
        {
            Notifications.Add(notification);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingAlertService : IAlertService
    {
        public Task SendAsync(AlertNotification notification, CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("Alert provider failure.");
        }
    }
}
