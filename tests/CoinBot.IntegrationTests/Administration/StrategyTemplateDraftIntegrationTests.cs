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
            Assert.Equal(1, draft.TemplateRevisionNumber);
            Assert.Equal("Valid", draft.ValidationStatusCode);

            var usageRow = Assert.Single(snapshot.UsageRows);
            Assert.Equal("template-draft-int", usageRow.StrategyKey);
            Assert.Equal("rsi-reversal", usageRow.TemplateKey);
            Assert.Equal("RSI Reversal", usageRow.TemplateName);
            Assert.Equal("r1", usageRow.RuntimeTemplateRevisionLabel);
            Assert.Equal("Valid", usageRow.ValidationStatusCode);
            Assert.Contains(snapshot.TemplateCatalog, template => template.TemplateKey == "rsi-reversal" && template.ValidationStatusCode == "Valid" && template.ActiveRevisionNumber == 1);
        }
        finally
        {
            await dbContext.Database.EnsureDeletedAsync();
        }
    }

    [Fact]
    public async Task ArchivedCustomTemplateRevision_CannotCreateRuntimeDraft_OnSqlServer()
    {
        var databaseName = $"CoinBotStrategyTemplateArchiveInt_{Guid.NewGuid():N}";
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
                Id = "template-archive-user",
                UserName = "template-archive-user",
                NormalizedUserName = "TEMPLATE-ARCHIVE-USER",
                Email = "template-archive-user@coinbot.test",
                NormalizedEmail = "TEMPLATE-ARCHIVE-USER@COINBOT.TEST",
                FullName = "Template Archive User",
                EmailConfirmed = true
            });
            dbContext.TradingStrategies.Add(new TradingStrategy
            {
                Id = strategyId,
                OwnerUserId = "template-archive-user",
                StrategyKey = "template-archive-int",
                DisplayName = "Template Archive Integration",
                PromotionState = StrategyPromotionState.Draft
            });
            await dbContext.SaveChangesAsync();

            var parser = new StrategyRuleParser();
            var validator = new StrategyDefinitionValidator();
            var templateCatalog = new StrategyTemplateCatalogService(parser, validator, dbContext);
            var strategyVersionService = new StrategyVersionService(
                dbContext,
                parser,
                new FixedTimeProvider(nowUtc),
                validator,
                templateCatalog);

            _ = await templateCatalog.CreateCustomAsync(
                "template-archive-user",
                "archived-runtime-template",
                "Archived Runtime Template",
                "Custom archived template.",
                "Custom",
                CreateCustomDefinitionJson(revision: 1));
            _ = await templateCatalog.ReviseAsync(
                "archived-runtime-template",
                "Archived Runtime Template",
                "Custom archived template revised.",
                "Custom",
                CreateCustomDefinitionJson(revision: 2));
            _ = await templateCatalog.ArchiveAsync("archived-runtime-template");

            var exception = await Assert.ThrowsAsync<StrategyTemplateCatalogException>(() => strategyVersionService.CreateDraftFromTemplateAsync(strategyId, "archived-runtime-template"));
            var templateId = await dbContext.TradingStrategyTemplates
                .AsNoTracking()
                .Where(entity => entity.TemplateKey == "archived-runtime-template")
                .Select(entity => entity.Id)
                .SingleAsync();
            var revisions = await dbContext.TradingStrategyTemplateRevisions
                .AsNoTracking()
                .Where(entity => entity.TradingStrategyTemplateId == templateId)
                .ToListAsync();

            Assert.Equal("TemplateArchived", exception.FailureCode);
            Assert.Contains("archived", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.All(revisions, revision => Assert.True(revision.ArchivedAtUtc.HasValue));
        }
        finally
        {
            await dbContext.Database.EnsureDeletedAsync();
        }
    }

    [Fact]
    public async Task CreateDraftFromTemplateAsync_UsesPublishedRevision_AndPreviousDraftsRemainImmutable_OnSqlServer()
    {
        var databaseName = $"CoinBotStrategyTemplatePublishedCloneInt_{Guid.NewGuid():N}";
        var connectionString = SqlServerIntegrationDatabase.ResolveConnectionString(databaseName);
        var nowUtc = new DateTimeOffset(2026, 4, 8, 12, 0, 0, TimeSpan.Zero);
        await using var dbContext = CreateDbContext(connectionString);
        await dbContext.Database.EnsureDeletedAsync();
        await dbContext.Database.EnsureCreatedAsync();

        try
        {
            var strategyId = Guid.NewGuid();
            dbContext.Users.Add(new ApplicationUser
            {
                Id = "template-publish-user",
                UserName = "template-publish-user",
                NormalizedUserName = "TEMPLATE-PUBLISH-USER",
                Email = "template-publish-user@coinbot.test",
                NormalizedEmail = "TEMPLATE-PUBLISH-USER@COINBOT.TEST",
                FullName = "Template Publish User",
                EmailConfirmed = true
            });
            dbContext.TradingStrategies.Add(new TradingStrategy
            {
                Id = strategyId,
                OwnerUserId = "template-publish-user",
                StrategyKey = "template-published-clone-int",
                DisplayName = "Template Published Clone Integration",
                PromotionState = StrategyPromotionState.Draft
            });
            await dbContext.SaveChangesAsync();

            var parser = new StrategyRuleParser();
            var validator = new StrategyDefinitionValidator();
            var templateCatalog = new StrategyTemplateCatalogService(parser, validator, dbContext);
            var strategyVersionService = new StrategyVersionService(
                dbContext,
                parser,
                new FixedTimeProvider(nowUtc),
                validator,
                templateCatalog);

            _ = await templateCatalog.CreateCustomAsync(
                "template-publish-user",
                "published-clone-template",
                "Published Clone Template",
                "Initial published template.",
                "Custom",
                CreateCustomDefinitionJson(revision: 1));
            _ = await templateCatalog.ReviseAsync(
                "published-clone-template",
                "Published Clone Template",
                "Draft revision two.",
                "Custom",
                CreateCustomDefinitionJson(revision: 2));

            var firstDraft = await strategyVersionService.CreateDraftFromTemplateAsync(strategyId, "published-clone-template");
            _ = await templateCatalog.PublishAsync("published-clone-template", 2);
            var secondDraft = await strategyVersionService.CreateDraftFromTemplateAsync(strategyId, "published-clone-template");
            var persistedVersions = await dbContext.TradingStrategyVersions
                .AsNoTracking()
                .OrderBy(entity => entity.VersionNumber)
                .ToListAsync();

            Assert.Equal(1, firstDraft.TemplateRevisionNumber);
            Assert.Equal(2, secondDraft.TemplateRevisionNumber);
            Assert.Contains("\"templateRevisionNumber\": 1", persistedVersions[0].DefinitionJson, StringComparison.Ordinal);
            Assert.Contains("\"templateRevisionNumber\": 2", persistedVersions[1].DefinitionJson, StringComparison.Ordinal);
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

    private static string CreateCustomDefinitionJson(int revision)
    {
        return
            $$"""
            {
              "schemaVersion": 2,
              "metadata": {
                "templateKey": "archived-runtime-template",
                "templateName": "Archived Runtime Template",
                "templateRevisionNumber": {{revision}},
                "templateSource": "Custom"
              },
              "entry": {
                "path": "context.mode",
                "comparison": "equals",
                "value": "Demo",
                "ruleId": "entry-mode",
                "ruleType": "context",
                "timeframe": "30m",
                "weight": 20,
                "enabled": true
              }
            }
            """;
    }
}


