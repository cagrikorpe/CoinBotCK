using CoinBot.Application.Abstractions.Alerts;
using CoinBot.Infrastructure.Alerts;
using Microsoft.Extensions.Logging.Abstractions;

namespace CoinBot.UnitTests.Infrastructure.Alerts;

public sealed class AlertingServiceTests
{
    [Fact]
    public async Task SendAsync_DispatchesOnlyToEnabledProviders_AndSwallowsProviderFailures()
    {
        var enabledProvider = new FakeAlertProvider("enabled-provider", isEnabled: true);
        var disabledProvider = new FakeAlertProvider("disabled-provider", isEnabled: false);
        var failingProvider = new FakeAlertProvider("failing-provider", isEnabled: true, throwOnSend: true);
        var service = new AlertingService(
            [enabledProvider, disabledProvider, failingProvider],
            NullLogger<AlertingService>.Instance);

        await service.SendAsync(
            new AlertNotification(
                Code: "DEGRADED_MODE_STOPPED_MARKETDATALATENCYCRITICAL",
                Severity: AlertSeverity.Critical,
                Title: "Execution stopped",
                Message: "Data latency threshold was exceeded."));

        Assert.Equal(1, enabledProvider.SendCount);
        Assert.Equal(0, disabledProvider.SendCount);
        Assert.Equal(1, failingProvider.SendCount);
    }

    private sealed class FakeAlertProvider(string name, bool isEnabled, bool throwOnSend = false) : IAlertProvider
    {
        public string Name { get; } = name;

        public bool IsEnabled { get; } = isEnabled;

        public int SendCount { get; private set; }

        public Task SendAsync(AlertNotification notification, CancellationToken cancellationToken = default)
        {
            SendCount++;

            if (throwOnSend)
            {
                throw new InvalidOperationException("Provider failure");
            }

            return Task.CompletedTask;
        }
    }
}