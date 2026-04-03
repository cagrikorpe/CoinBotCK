using CoinBot.Application.Abstractions.DataScope;
using CoinBot.Application.Abstractions.MarketData;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.MarketData;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CoinBot.UnitTests.Infrastructure.MarketData;

public sealed class MarketScannerWorkerTests
{
    [Fact]
    public async Task RunOnceAsync_ExecutesScannerServiceAndPersistsCycle()
    {
        var nowUtc = new DateTimeOffset(2026, 4, 3, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new FixedTimeProvider(nowUtc);
        var services = CreateServices(timeProvider, out var dbContextOptions);

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var seedContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        seedContext.HistoricalMarketCandles.Add(new HistoricalMarketCandle
        {
            Id = Guid.NewGuid(),
            Symbol = "BTCUSDT",
            Interval = "1m",
            OpenTimeUtc = new DateTime(2026, 4, 3, 11, 59, 0, DateTimeKind.Utc),
            CloseTimeUtc = nowUtc.UtcDateTime,
            OpenPrice = 100m,
            HighPrice = 100m,
            LowPrice = 100m,
            ClosePrice = 100m,
            Volume = 100_000m,
            ReceivedAtUtc = nowUtc.UtcDateTime,
            Source = "unit-test"
        });
        await seedContext.SaveChangesAsync();

        var worker = new MarketScannerWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new MarketScannerOptions { ScanIntervalSeconds = 30, Min24hQuoteVolume = 100m }),
            timeProvider,
            NullLogger<MarketScannerWorker>.Instance);

        await worker.RunOnceAsync();

        await using var verifyContext = new ApplicationDbContext(dbContextOptions, new TestDataScopeContext());
        var cycle = await verifyContext.MarketScannerCycles.SingleAsync();
        var candidate = await verifyContext.MarketScannerCandidates.SingleAsync(entity => entity.ScanCycleId == cycle.Id);
        var heartbeat = await verifyContext.WorkerHeartbeats.SingleAsync(entity => entity.WorkerKey == MarketScannerService.WorkerKey);

        Assert.Equal("BTCUSDT", candidate.Symbol);
        Assert.True(candidate.IsEligible);
        Assert.Equal(1, candidate.Rank);
        Assert.Equal(MonitoringHealthState.Healthy, heartbeat.HealthState);
    }

    [Fact]
    public async Task RecordFailureAsync_PersistsCriticalHeartbeat()
    {
        var nowUtc = new DateTimeOffset(2026, 4, 3, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new FixedTimeProvider(nowUtc);
        var services = CreateServices(timeProvider, out var dbContextOptions);

        using var provider = services.BuildServiceProvider();
        var worker = new MarketScannerWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new MarketScannerOptions()),
            timeProvider,
            NullLogger<MarketScannerWorker>.Instance);

        await worker.RecordFailureAsync(new InvalidOperationException("scanner boom"));

        await using var verifyContext = new ApplicationDbContext(dbContextOptions, new TestDataScopeContext());
        var heartbeat = await verifyContext.WorkerHeartbeats.SingleAsync(entity => entity.WorkerKey == MarketScannerService.WorkerKey);

        Assert.Equal(MonitoringHealthState.Critical, heartbeat.HealthState);
        Assert.Equal("InvalidOperationException", heartbeat.LastErrorCode);
        Assert.Contains("scanner boom", heartbeat.Detail ?? string.Empty, StringComparison.Ordinal);
    }

    private static ServiceCollection CreateServices(TimeProvider timeProvider, out DbContextOptions<ApplicationDbContext> dbContextOptions)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;


        dbContextOptions = options;
        var services = new ServiceCollection();
        services.AddScoped(_ => new ApplicationDbContext(options, new TestDataScopeContext()));
        services.AddLogging();
        services.AddSingleton<IMarketDataService>(new FakeMarketDataService());
        services.AddSingleton<ISharedSymbolRegistry>(new FakeSharedSymbolRegistry([
            new SymbolMetadataSnapshot("BTCUSDT", "Binance", "BTC", "USDT", 0.1m, 0.001m, "TRADING", true, timeProvider.GetUtcNow().UtcDateTime)
        ]));
        services.AddSingleton(timeProvider);
        services.AddSingleton(Options.Create(new MarketScannerOptions
        {
            TopCandidateCount = 5,
            Min24hQuoteVolume = 100m,
            MaxDataAgeSeconds = 120,
            AllowedQuoteAssets = ["USDT"]
        }));
        services.AddSingleton(Options.Create(new BinanceMarketDataOptions
        {
            KlineInterval = "1m",
            SeedSymbols = ["BTCUSDT"]
        }));
        services.AddScoped<MarketScannerService>();

        return services;
    }

    private sealed class TestDataScopeContext : IDataScopeContext
    {
        public string? UserId => null;

        public bool HasIsolationBypass => true;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeSharedSymbolRegistry(IReadOnlyCollection<SymbolMetadataSnapshot> snapshots) : ISharedSymbolRegistry
    {
        public ValueTask<SymbolMetadataSnapshot?> GetSymbolAsync(string symbol, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(snapshots.SingleOrDefault(item => item.Symbol == symbol));
        }

        public ValueTask<IReadOnlyCollection<SymbolMetadataSnapshot>> ListSymbolsAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(snapshots);
        }
    }

    private sealed class FakeMarketDataService : IMarketDataService
    {
        public ValueTask TrackSymbolAsync(string symbol, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask TrackSymbolsAsync(IEnumerable<string> symbols, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask<MarketPriceSnapshot?> GetLatestPriceAsync(string symbol, CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult<MarketPriceSnapshot?>(new MarketPriceSnapshot(symbol, 100m, DateTime.UtcNow, DateTime.UtcNow, "unit-test"));
        }

        public ValueTask<SymbolMetadataSnapshot?> GetSymbolMetadataAsync(string symbol, CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult<SymbolMetadataSnapshot?>(null);
        }

        public async IAsyncEnumerable<MarketPriceSnapshot> WatchAsync(
            IEnumerable<string> symbols,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
