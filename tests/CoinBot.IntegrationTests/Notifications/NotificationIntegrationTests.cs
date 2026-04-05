using CoinBot.Application.Abstractions.Alerts;
using CoinBot.Infrastructure.Alerts;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace CoinBot.IntegrationTests.Notifications;

public sealed class NotificationIntegrationTests
{
    [Fact]
    public async Task AlertDispatchCoordinator_DedupesRepeatedOperationalEvents()
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
            "EventType=OrderRejected; Symbol=BTCUSDT; Result=Rejected; FailureCode=OrderNotionalBelowMinimum; TimestampUtc=2026-04-02T10:00:00.0000000Z; Environment=Development/Testnet");

        await coordinator.SendAsync(notification, "integration-order-transition-1", TimeSpan.FromMinutes(5));
        await coordinator.SendAsync(notification, "integration-order-transition-1", TimeSpan.FromMinutes(5));

        Assert.Single(alertService.Notifications);
        Assert.Equal("ORDER_REJECTED", alertService.Notifications[0].Code);
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
}
