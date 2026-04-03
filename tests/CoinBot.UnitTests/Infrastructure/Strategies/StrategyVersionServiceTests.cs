using CoinBot.Application.Abstractions.DataScope;
using CoinBot.Application.Abstractions.Strategies;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Persistence;
using CoinBot.Infrastructure.Strategies;
using CoinBot.UnitTests.Infrastructure.Mfa;
using Microsoft.EntityFrameworkCore;

namespace CoinBot.UnitTests.Infrastructure.Strategies;

public sealed class StrategyVersionServiceTests
{
    [Fact]
    public async Task CreateDraftAsync_ArchivesPreviousDraft_AndIncrementsVersionNumber()
    {
        var timeProvider = new AdjustableTimeProvider(new DateTimeOffset(2026, 3, 22, 12, 0, 0, TimeSpan.Zero));
        await using var dbContext = CreateDbContext();
        var strategy = CreateStrategy("user-strategy-1", "momentum-core");

        dbContext.TradingStrategies.Add(strategy);
        await dbContext.SaveChangesAsync();

        var service = CreateService(dbContext, timeProvider);
        var firstDraft = await service.CreateDraftAsync(strategy.Id, CreateDefinitionJson("Demo"));

        timeProvider.Advance(TimeSpan.FromMinutes(5));

        var secondDraft = await service.CreateDraftAsync(strategy.Id, CreateDefinitionJson("Live"));
        var persistedVersions = await dbContext.TradingStrategyVersions
            .OrderBy(entity => entity.VersionNumber)
            .ToListAsync();

        Assert.Equal(1, firstDraft.VersionNumber);
        Assert.Equal(2, secondDraft.VersionNumber);
        Assert.Equal(StrategyVersionStatus.Archived, persistedVersions[0].Status);
        Assert.Equal(timeProvider.GetUtcNow().UtcDateTime, persistedVersions[0].ArchivedAtUtc);
        Assert.Equal(StrategyVersionStatus.Draft, persistedVersions[1].Status);
        Assert.Equal(strategy.OwnerUserId, persistedVersions[1].OwnerUserId);
        Assert.Equal(2, persistedVersions[1].SchemaVersion);
        Assert.Equal("Valid", secondDraft.ValidationStatusCode);
    }

    [Fact]
    public async Task CreateDraftFromTemplateAsync_CreatesValidatedDraft_WithTemplateMetadata()
    {
        await using var dbContext = CreateDbContext();
        var strategy = CreateStrategy("user-template-1", "rsi-template-core");
        dbContext.TradingStrategies.Add(strategy);
        await dbContext.SaveChangesAsync();

        var service = CreateService(dbContext, new AdjustableTimeProvider(new DateTimeOffset(2026, 3, 22, 12, 0, 0, TimeSpan.Zero)));

        var draft = await service.CreateDraftFromTemplateAsync(strategy.Id, "rsi-reversal");
        var persistedVersion = await dbContext.TradingStrategyVersions.SingleAsync(entity => entity.Id == draft.StrategyVersionId);

        Assert.Equal("rsi-reversal", draft.TemplateKey);
        Assert.Equal("RSI Reversal", draft.TemplateName);
        Assert.Equal("Valid", draft.ValidationStatusCode);
        Assert.Equal(2, persistedVersion.SchemaVersion);
        Assert.Contains("\"templateKey\": \"rsi-reversal\"", persistedVersion.DefinitionJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateDraftAsync_RejectsInvalidStrategyDefinition_WithExactValidationReason()
    {
        await using var dbContext = CreateDbContext();
        var strategy = CreateStrategy("user-invalid-1", "invalid-core");
        dbContext.TradingStrategies.Add(strategy);
        await dbContext.SaveChangesAsync();

        var service = CreateService(dbContext, new AdjustableTimeProvider(new DateTimeOffset(2026, 3, 22, 12, 0, 0, TimeSpan.Zero)));

        var exception = await Assert.ThrowsAsync<StrategyDefinitionValidationException>(() => service.CreateDraftAsync(
            strategy.Id,
            """
            {
              "schemaVersion": 2,
              "entry": {
                "path": "indicator.rsi.value",
                "comparison": "lessThanOrEqual",
                "value": 101,
                "ruleId": "entry-rsi",
                "ruleType": "rsi",
                "weight": 10,
                "enabled": true
              }
            }
            """));

        Assert.Equal("InvalidRsiThreshold:entry:101", exception.StatusCode);
        Assert.Contains("InvalidRsiThreshold", exception.Message, StringComparison.Ordinal);
        Assert.Empty(await dbContext.TradingStrategyVersions.ToListAsync());
    }

    [Fact]
    public async Task PublishAsync_ArchivesPreviousPublishedVersion_AndDoesNotChangeExecutionPromotionState()
    {
        var timeProvider = new AdjustableTimeProvider(new DateTimeOffset(2026, 3, 22, 12, 0, 0, TimeSpan.Zero));
        await using var dbContext = CreateDbContext();
        var strategy = CreateStrategy("user-strategy-2", "breakout-core");
        strategy.PromotionState = StrategyPromotionState.DemoPublished;
        strategy.PublishedMode = ExecutionEnvironment.Demo;
        strategy.PublishedAtUtc = new DateTime(2026, 3, 22, 11, 45, 0, DateTimeKind.Utc);

        dbContext.TradingStrategies.Add(strategy);
        await dbContext.SaveChangesAsync();

        var service = CreateService(dbContext, timeProvider);
        var firstDraft = await service.CreateDraftAsync(strategy.Id, CreateDefinitionJson("Demo"));
        var firstPublished = await service.PublishAsync(firstDraft.StrategyVersionId);

        timeProvider.Advance(TimeSpan.FromMinutes(5));

        var secondDraft = await service.CreateDraftAsync(strategy.Id, CreateDefinitionJson("Demo"));
        var secondPublished = await service.PublishAsync(secondDraft.StrategyVersionId);
        var persistedStrategy = await dbContext.TradingStrategies.SingleAsync(entity => entity.Id == strategy.Id);
        var persistedVersions = await dbContext.TradingStrategyVersions
            .OrderBy(entity => entity.VersionNumber)
            .ToListAsync();

        Assert.Equal(StrategyVersionStatus.Published, firstPublished.Status);
        Assert.Equal(StrategyVersionStatus.Published, secondPublished.Status);
        Assert.Equal(StrategyVersionStatus.Archived, persistedVersions[0].Status);
        Assert.Equal(timeProvider.GetUtcNow().UtcDateTime, persistedVersions[0].ArchivedAtUtc);
        Assert.Equal(StrategyVersionStatus.Published, persistedVersions[1].Status);
        Assert.Equal(timeProvider.GetUtcNow().UtcDateTime, persistedVersions[1].PublishedAtUtc);
        Assert.Equal(StrategyPromotionState.DemoPublished, persistedStrategy.PromotionState);
        Assert.Equal(ExecutionEnvironment.Demo, persistedStrategy.PublishedMode);
    }

    private static StrategyVersionService CreateService(ApplicationDbContext dbContext, TimeProvider timeProvider)
    {
        var parser = new StrategyRuleParser();
        var validator = new StrategyDefinitionValidator();
        return new StrategyVersionService(
            dbContext,
            parser,
            timeProvider,
            validator,
            new StrategyTemplateCatalogService(parser, validator));
    }

    private static ApplicationDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        return new ApplicationDbContext(options, new TestDataScopeContext());
    }

    private static TradingStrategy CreateStrategy(string ownerUserId, string strategyKey)
    {
        return new TradingStrategy
        {
            OwnerUserId = ownerUserId,
            StrategyKey = strategyKey,
            DisplayName = $"{strategyKey} strategy"
        };
    }

    private static string CreateDefinitionJson(string mode)
    {
        return
            $$"""
            {
              "schemaVersion": 2,
              "metadata": {
                "templateKey": "custom",
                "templateName": "Custom Strategy"
              },
              "entry": {
                "operator": "all",
                "ruleId": "entry-root",
                "ruleType": "group",
                "timeframe": "1m",
                "weight": 1,
                "enabled": true,
                "rules": [
                  {
                    "ruleId": "entry-mode",
                    "ruleType": "context",
                    "path": "context.mode",
                    "comparison": "equals",
                    "value": "{{mode}}",
                    "timeframe": "1m",
                    "weight": 10,
                    "enabled": true
                  },
                  {
                    "ruleId": "risk-rsi-ready",
                    "ruleType": "rsi",
                    "path": "indicator.rsi.isReady",
                    "comparison": "equals",
                    "value": true,
                    "timeframe": "1m",
                    "weight": 5,
                    "enabled": true
                  }
                ]
              }
            }
            """;
    }

    private sealed class TestDataScopeContext : IDataScopeContext
    {
        public string? UserId => null;

        public bool HasIsolationBypass => true;
    }
}

