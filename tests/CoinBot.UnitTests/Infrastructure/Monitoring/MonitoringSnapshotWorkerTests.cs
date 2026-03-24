using CoinBot.Application.Abstractions.Alerts;
using CoinBot.Application.Abstractions.DataScope;
using CoinBot.Application.Abstractions.Execution;
using CoinBot.Application.Abstractions.MarketData;
using CoinBot.Application.Abstractions.Monitoring;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Execution;
using CoinBot.Infrastructure.MarketData;
using CoinBot.Infrastructure.Monitoring;
using CoinBot.Infrastructure.Persistence;
using CoinBot.UnitTests.Infrastructure.Mfa;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CoinBot.UnitTests.Infrastructure.Monitoring;

public sealed class MonitoringSnapshotWorkerTests
{
    [Fact]
    public async Task RunWarmCycleAsync_UsesExchangeServerTime_ForClockDriftMonitor()
    {
        var now = new DateTimeOffset(2026, 3, 24, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new AdjustableTimeProvider(now);
        var databaseRoot = new InMemoryDatabaseRoot();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(timeProvider);
        services.AddSingleton<TimeProvider>(timeProvider);
        services.AddSingleton(databaseRoot);
        services.AddSingleton<IMonitoringTelemetryCollector, FakeMonitoringTelemetryCollector>();
        services.AddSingleton<IRedisLatencyProbe, FakeRedisLatencyProbe>();
        services.AddSingleton<IBinanceExchangeInfoClient>(new FakeExchangeInfoClient(now.UtcDateTime.AddSeconds(-5)));
        services.AddSingleton<IAlertService, FakeAlertService>();
        services.AddSingleton<IOptions<DataLatencyGuardOptions>>(Options.Create(new DataLatencyGuardOptions()));
        services.AddScoped<IDataScopeContextAccessor, FakeDataScopeContextAccessor>();
        services.AddScoped<IDataScopeContext>(serviceProvider => serviceProvider.GetRequiredService<IDataScopeContextAccessor>());
        services.AddDbContext<ApplicationDbContext>(options =>
            options.UseInMemoryDatabase("MonitoringSnapshotWorkerTests", databaseRoot));
        services.AddScoped<IDataLatencyCircuitBreaker, DataLatencyCircuitBreaker>();

        await using var provider = services.BuildServiceProvider();
        var worker = new MonitoringSnapshotWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<IMonitoringTelemetryCollector>(),
            provider.GetRequiredService<IRedisLatencyProbe>(),
            provider.GetRequiredService<IBinanceExchangeInfoClient>(),
            provider.GetRequiredService<IOptions<DataLatencyGuardOptions>>(),
            timeProvider,
            NullLogger<MonitoringSnapshotWorker>.Instance);

        await worker.RunWarmCycleAsync();

        await using var scope = provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var clockDriftSnapshot = await dbContext.HealthSnapshots
            .AsNoTracking()
            .SingleAsync(entity => entity.SnapshotKey == "clock-drift-monitor");

        Assert.Equal(MonitoringHealthState.Critical, clockDriftSnapshot.HealthState);
        Assert.Equal("ClockDriftMs=5000; LocalClockUtc=2026-03-24T12:00:00.0000000Z; ExchangeServerTimeUtc=2026-03-24T11:59:55.0000000Z; Probe=Succeeded", clockDriftSnapshot.Detail);
    }

    private sealed class FakeExchangeInfoClient(DateTime serverTimeUtc) : IBinanceExchangeInfoClient
    {
        public Task<IReadOnlyCollection<SymbolMetadataSnapshot>> GetSymbolMetadataAsync(
            IReadOnlyCollection<string> symbols,
            CancellationToken cancellationToken = default)
        {
            _ = symbols;
            return Task.FromResult<IReadOnlyCollection<SymbolMetadataSnapshot>>([]);
        }

        public Task<DateTime?> GetServerTimeUtcAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<DateTime?>(serverTimeUtc);
        }
    }

    private sealed class FakeMonitoringTelemetryCollector : IMonitoringTelemetryCollector
    {
        private TimeSpan? databaseLatency;
        private TimeSpan? redisLatency;

        public void RecordBinancePing(TimeSpan latency, int? rateLimitUsage = null, DateTime? observedAtUtc = null)
        {
        }

        public void RecordWebSocketActivity(DateTime lastMessageAtUtc, int reconnectCount, int streamGapCount, int? lastMessageAgeSeconds = null, int? staleDurationSeconds = null)
        {
        }

        public void RecordSignalRConnectionCount(int activeConnectionCount, DateTime? observedAtUtc = null)
        {
        }

        public void AdjustSignalRConnectionCount(int delta, DateTime? observedAtUtc = null)
        {
        }

        public void RecordDatabaseLatency(TimeSpan latency, DateTime? observedAtUtc = null)
        {
            databaseLatency = latency;
        }

        public void RecordRedisLatency(TimeSpan? latency, DateTime? observedAtUtc = null)
        {
            redisLatency = latency;
        }

        public MonitoringTelemetrySnapshot CaptureSnapshot(DateTime? capturedAtUtc = null)
        {
            return new MonitoringTelemetrySnapshot(
                capturedAtUtc ?? DateTime.UtcNow,
                null,
                null,
                null,
                null,
                null,
                null,
                0,
                0,
                databaseLatency is null ? null : (int)Math.Round(databaseLatency.Value.TotalMilliseconds),
                capturedAtUtc,
                redisLatency is null ? null : (int)Math.Round(redisLatency.Value.TotalMilliseconds),
                capturedAtUtc,
                0);
        }
    }

    private sealed class FakeRedisLatencyProbe : IRedisLatencyProbe
    {
        public Task<RedisLatencyProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new RedisLatencyProbeResult(
                RedisProbeStatus.Succeeded,
                TimeSpan.FromMilliseconds(7),
                "127.0.0.1:6379",
                null));
        }
    }

    private sealed class FakeAlertService : IAlertService
    {
        public Task SendAsync(AlertNotification notification, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class FakeDataScopeContextAccessor : IDataScopeContextAccessor
    {
        private string? userId;
        private bool hasIsolationBypass;

        public string? UserId => userId;

        public bool HasIsolationBypass => hasIsolationBypass;

        public IDisposable BeginScope(string? userId = null, bool hasIsolationBypass = false)
        {
            this.userId = userId?.Trim();
            this.hasIsolationBypass = hasIsolationBypass;
            return new ScopeLease(() =>
            {
                this.userId = null;
                this.hasIsolationBypass = false;
            });
        }

        private sealed class ScopeLease(Action onDispose) : IDisposable
        {
            public void Dispose()
            {
                onDispose();
            }
        }
    }
}
