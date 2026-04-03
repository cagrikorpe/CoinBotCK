using CoinBot.Application.Abstractions.Execution;
using CoinBot.Application.Abstractions.Monitoring;
using CoinBot.Application.Abstractions.Strategies;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Administration;
using CoinBot.Infrastructure.Identity;
using CoinBot.Infrastructure.Persistence;
using CoinBot.Infrastructure.Strategies;
using CoinBot.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace CoinBot.IntegrationTests.Administration;

public sealed class StrategyTemplateDraftIntegrationTests
{
    [Fact]
    public async Task CreateDraftFromTemplateAsync_PersistsValidatedDraftAndAdminProjection_OnSqlServer()
    {
        var databaseName = $"CoinBotStrategyTemplateDraftInt_{Guid.NewGuid():N}";
        var connectionString = SqlServerIntegrationDatabase.ResolveConnectionString(databaseName);
        var nowUtc = new DateTimeOffset(2026, 4, 3, 12, 0, 0, TimeSpan.Zero);
        await using var dbContext = CreateDbContext(connectionString);
        await dbContext.Database.EnsureDeletedAsync();
        await dbContext.Database.EnsureCreatedAsync();

        try
        {
            var strategyId = Guid.NewGuid();
            dbContext.Users.Add(new ApplicationUser
            {
                Id = "template-user",
                UserName = "template-user",
                NormalizedUserName = "TEMPLATE-USER",
                Email = "template-user@coinbot.test",
                NormalizedEmail = "TEMPLATE-USER@COINBOT.TEST",
                FullName = "Template Draft User",
                EmailConfirmed = true
            });
            dbContext.TradingStrategies.Add(new TradingStrategy
            {
                Id = strategyId,
                OwnerUserId = "template-user",
                StrategyKey = "template-draft-int",
                DisplayName = "Template Draft Integration",
                PromotionState = StrategyPromotionState.Draft,
                CreatedDate = nowUtc.UtcDateTime.AddMinutes(-5),
                UpdatedDate = nowUtc.UtcDateTime.AddMinutes(-5)
            });
            await dbContext.SaveChangesAsync();

            var parser = new StrategyRuleParser();
            var validator = new StrategyDefinitionValidator();
            var templateCatalog = new StrategyTemplateCatalogService(parser, validator);
            var strategyVersionService = new StrategyVersionService(
                dbContext,
                parser,
                new FixedTimeProvider(nowUtc),
                validator,
                templateCatalog);

            var draft = await strategyVersionService.CreateDraftFromTemplateAsync(strategyId, "rsi-reversal");

            var persistedVersion = await dbContext.TradingStrategyVersions
                .AsNoTracking()
                .SingleAsync(entity => entity.Id == draft.StrategyVersionId);
            var readModelService = new AdminWorkspaceReadModelService(
                dbContext,
                new FakeAdminMonitoringReadModelService(nowUtc.UtcDateTime),
                new FakeTradingModeResolver(),
                new FixedTimeProvider(nowUtc),
                parser,
                validator,
                templateCatalog);
            var snapshot = await readModelService.GetStrategyAiMonitoringAsync("template-draft-int");

            Assert.Equal(StrategyVersionStatus.Draft, persistedVersion.Status);
            Assert.Equal(2, persistedVersion.SchemaVersion);
            Assert.Equal("rsi-reversal", draft.TemplateKey);
            Assert.Equal("RSI Reversal", draft.TemplateName);
            Assert.Equal("Valid", draft.ValidationStatusCode);

            var usageRow = Assert.Single(snapshot.UsageRows);
            Assert.Equal("template-draft-int", usageRow.StrategyKey);
            Assert.Equal("rsi-reversal", usageRow.TemplateKey);
            Assert.Equal("RSI Reversal", usageRow.TemplateName);
            Assert.Equal("Valid", usageRow.ValidationStatusCode);
            Assert.Contains(snapshot.TemplateCatalog, template => template.TemplateKey == "rsi-reversal" && template.ValidationStatusCode == "Valid");
        }
        finally
        {
            await dbContext.Database.EnsureDeletedAsync();
        }
    }

    private static ApplicationDbContext CreateDbContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer(connectionString)
            .Options;

        return new ApplicationDbContext(options, new TestDataScopeContext());
    }

    private sealed class TestDataScopeContext : CoinBot.Application.Abstractions.DataScope.IDataScopeContext
    {
        public string? UserId => null;

        public bool HasIsolationBypass => true;
    }

    private sealed class FakeAdminMonitoringReadModelService(DateTime nowUtc) : CoinBot.Application.Abstractions.Administration.IAdminMonitoringReadModelService
    {
        public Task<MonitoringDashboardSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(MonitoringDashboardSnapshot.Empty(nowUtc));
        }
    }

    private sealed class FakeTradingModeResolver : CoinBot.Application.Abstractions.Execution.ITradingModeResolver
    {
        public Task<TradingModeResolution> ResolveAsync(
            TradingModeResolutionRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new TradingModeResolution(
                ExecutionEnvironment.Demo,
                null,
                null,
                null,
                ExecutionEnvironment.Demo,
                TradingModeResolutionSource.GlobalDefault,
                "integration-test",
                false));
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset nowUtc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => nowUtc;
    }
}
