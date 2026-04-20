using CoinBot.Application.Abstractions.Auditing;
using CoinBot.Application.Abstractions.DataScope;
using CoinBot.Application.Abstractions.MarketData;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Auditing;
using CoinBot.Infrastructure.DemoPortfolio;
using CoinBot.Infrastructure.Identity;
using CoinBot.Infrastructure.Observability;
using CoinBot.Infrastructure.Persistence;
using CoinBot.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CoinBot.IntegrationTests.Execution;

public sealed class DemoSessionServiceIntegrationTests
{
    [Fact]
    public async Task RunConsistencyCheckAsync_RefreshesStaleBotCounter_OnSqlServer()
    {
        var connectionString = SqlServerIntegrationDatabase.ResolveConnectionString($"CoinBotDemoSessionCounter_{Guid.NewGuid():N}");
        var botId = Guid.NewGuid();

        await using var harness = CreateHarness(connectionString);

        try
        {
            await harness.DbContext.Database.EnsureDeletedAsync();
            await harness.DbContext.Database.MigrateAsync();
            harness.DbContext.Users.Add(new ApplicationUser
            {
                Id = "user-demo-counter-sql",
                UserName = "user-demo-counter-sql",
                NormalizedUserName = "USER-DEMO-COUNTER-SQL",
                Email = "user-demo-counter-sql@coinbot.test",
                NormalizedEmail = "USER-DEMO-COUNTER-SQL@COINBOT.TEST",
                FullName = "Demo Counter SQL",
                EmailConfirmed = true
            });
            harness.DbContext.TradingBots.Add(new TradingBot
            {
                Id = botId,
                OwnerUserId = "user-demo-counter-sql",
                Name = "Demo Counter Bot",
                StrategyKey = "demo-counter-sql",
                Symbol = "SOLUSDT",
                IsEnabled = true,
                OpenOrderCount = 0,
                OpenPositionCount = 1
            });
            harness.DbContext.DemoSessions.Add(new DemoSession
            {
                OwnerUserId = "user-demo-counter-sql",
                SequenceNumber = 1,
                SeedAsset = "USDT",
                SeedAmount = 10000m,
                State = DemoSessionState.Active,
                ConsistencyStatus = DemoConsistencyStatus.InSync,
                StartedAtUtc = new DateTime(2026, 4, 19, 4, 0, 0, DateTimeKind.Utc)
            });
            harness.DbContext.DemoPositions.Add(new DemoPosition
            {
                OwnerUserId = "user-demo-counter-sql",
                BotId = botId,
                PositionScopeKey = $"bot:{botId:N}",
                Symbol = "SOLUSDT",
                BaseAsset = "SOL",
                QuoteAsset = "USDT",
                Quantity = 0m,
                AverageEntryPrice = 0m
            });
            await harness.DbContext.SaveChangesAsync();

            var session = await harness.Service.RunConsistencyCheckAsync("user-demo-counter-sql");

            var bot = await harness.DbContext.TradingBots
                .IgnoreQueryFilters()
                .SingleAsync(entity => entity.Id == botId);
            Assert.NotNull(session);
            Assert.Equal(0, bot.OpenPositionCount);
            Assert.Equal(0, bot.OpenOrderCount);
        }
        finally
        {
            await SqlServerIntegrationDatabase.CleanupDatabaseAsync(connectionString);
        }
    }

    private static TestHarness CreateHarness(string connectionString)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer(connectionString)
            .Options;
        var dbContext = new ApplicationDbContext(options, new TestDataScopeContext());
        var auditLogService = new AuditLogService(dbContext, new CorrelationContextAccessor());
        var timeProvider = TimeProvider.System;
        var demoWalletValuationService = new DemoWalletValuationService(
            new FakeMarketDataService(),
            timeProvider,
            NullLogger<DemoWalletValuationService>.Instance);
        var service = new DemoSessionService(
            dbContext,
            new DemoConsistencyWatchdogService(
                dbContext,
                Options.Create(new DemoSessionOptions()),
                timeProvider,
                NullLogger<DemoConsistencyWatchdogService>.Instance),
            demoWalletValuationService,
            auditLogService,
            Options.Create(new DemoSessionOptions()),
            timeProvider,
            NullLogger<DemoSessionService>.Instance);

        return new TestHarness(dbContext, service);
    }

    private sealed class TestDataScopeContext : IDataScopeContext
    {
        public string? UserId => null;

        public bool HasIsolationBypass => true;
    }

    private sealed class FakeMarketDataService : IMarketDataService
    {
        public ValueTask TrackSymbolAsync(string symbol, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public ValueTask TrackSymbolsAsync(IEnumerable<string> symbols, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public ValueTask<MarketPriceSnapshot?> GetLatestPriceAsync(string symbol, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<MarketPriceSnapshot?>(null);
        }

        public ValueTask<SymbolMetadataSnapshot?> GetSymbolMetadataAsync(string symbol, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<SymbolMetadataSnapshot?>(null);
        }

        public async IAsyncEnumerable<MarketPriceSnapshot> WatchAsync(
            IEnumerable<string> symbols,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class TestHarness(ApplicationDbContext dbContext, DemoSessionService service) : IAsyncDisposable
    {
        public ApplicationDbContext DbContext { get; } = dbContext;

        public DemoSessionService Service { get; } = service;

        public async ValueTask DisposeAsync()
        {
            await DbContext.DisposeAsync();
        }
    }
}
