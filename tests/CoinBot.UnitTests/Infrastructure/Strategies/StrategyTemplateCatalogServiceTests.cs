using CoinBot.Application.Abstractions.DataScope;
using CoinBot.Application.Abstractions.Strategies;
using CoinBot.Contracts.Common;
using CoinBot.Infrastructure.Persistence;
using CoinBot.Infrastructure.Strategies;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace CoinBot.UnitTests.Infrastructure.Strategies;

public sealed class StrategyTemplateCatalogServiceTests
{
    [Fact]
    public async Task ListAsync_ReturnsValidatedBuiltInTemplates()
    {
        var service = new StrategyTemplateCatalogService(new StrategyRuleParser(), new StrategyDefinitionValidator());

        var templates = await service.ListAsync();

        Assert.True(templates.Count >= 3);
        Assert.Contains(templates, template => template.TemplateKey == "rsi-basic" && template.TemplateName == "RSI Basic" && template.Validation.IsValid && template.SchemaVersion == 2);
        Assert.Contains(templates, template => template.TemplateKey == "rsi-reversal" && template.Validation.IsValid && template.SchemaVersion == 2);
        Assert.All(templates, template =>
        {
            Assert.True(template.Validation.IsValid);
            Assert.False(string.IsNullOrWhiteSpace(template.Description));
        });
    }

    [Fact]
    public async Task GetAsync_ReturnsExactTemplateDefinition_ForKnownTemplateKey()
    {
        var service = new StrategyTemplateCatalogService(new StrategyRuleParser(), new StrategyDefinitionValidator());

        var template = await service.GetAsync("macd-trend");

        Assert.Equal("macd-trend", template.TemplateKey);
        Assert.Equal("MACD Trend", template.TemplateName);
        Assert.Contains("\"ruleType\": \"macd\"", template.DefinitionJson, StringComparison.Ordinal);
        Assert.Equal("Valid", template.Validation.StatusCode);
        Assert.True(template.IsBuiltIn);
    }

    [Fact]
    public async Task GetAsync_FailsClosed_WhenPersistenceIsUnavailable_ForUnknownCustomTemplate()
    {
        var service = new StrategyTemplateCatalogService(new StrategyRuleParser(), new StrategyDefinitionValidator());

        var exception = await Assert.ThrowsAsync<StrategyTemplateCatalogException>(() => service.GetAsync("unknown-template"));

        Assert.Equal("TemplatePersistenceUnavailable", exception.FailureCode);
        Assert.Contains("unknown-template", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetAsync_ReturnsRsiBasicBuiltIn_WithAlignedOneMinuteRuleGroups()
    {
        var parser = new StrategyRuleParser();
        var service = new StrategyTemplateCatalogService(parser, new StrategyDefinitionValidator());

        var template = await service.GetAsync("rsi-basic");
        var document = parser.Parse(template.DefinitionJson);
        var longEntry = Assert.IsType<StrategyRuleGroup>(document.LongEntry);
        var risk = Assert.IsType<StrategyRuleGroup>(document.Risk);

        Assert.Equal("RSI Basic", template.TemplateName);
        Assert.Equal(1, template.PublishedRevisionNumber);
        Assert.Equal("1m", longEntry.Metadata?.Timeframe);
        Assert.Equal("1m", risk.Metadata?.Timeframe);
        Assert.All(longEntry.Rules, rule => Assert.Equal("1m", ResolveTimeframe(rule)));
        Assert.All(risk.Rules, rule => Assert.Equal("1m", ResolveTimeframe(rule)));
        Assert.DoesNotContain("\"timeframe\": \"5m\"", template.DefinitionJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetAsync_FailsClosed_WhenPersistedTemplateKeyIsUnknown()
    {
        await using var dbContext = CreateDbContext();
        var service = CreateService(dbContext);

        var exception = await Assert.ThrowsAsync<StrategyTemplateCatalogException>(() => service.GetAsync("unknown-template"));

        Assert.Equal("TemplateNotFound", exception.FailureCode);
        Assert.Contains("unknown-template", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateCustomAsync_PersistsValidatedTemplate_AndListIncludesCustomEntry()
    {
        await using var dbContext = CreateDbContext();
        var service = CreateService(dbContext);

        var created = await service.CreateCustomAsync(
            "template-owner",
            "custom-rsi-live",
            "Custom RSI Live",
            "Custom template for live RSI entries.",
            "Custom",
            CreateTemplateDefinitionJson());
        var listedTemplates = await service.ListAsync();

        Assert.Equal("custom-rsi-live", created.TemplateKey);
        Assert.Equal("Custom RSI Live", created.TemplateName);
        Assert.False(created.IsBuiltIn);
        Assert.True(created.IsActive);
        Assert.Equal("Custom", created.TemplateSource);
        Assert.Equal(1, created.ActiveRevisionNumber);
        Assert.Equal(1, created.LatestRevisionNumber);
        Assert.Equal("Valid", created.Validation.StatusCode);
        Assert.Contains("\"templateKey\": \"custom-rsi-live\"", created.DefinitionJson, StringComparison.Ordinal);
        Assert.Contains(listedTemplates, template => template.TemplateKey == "custom-rsi-live" && !template.IsBuiltIn && template.TemplateSource == "Custom");
    }

    [Fact]
    public async Task CreateCustomAsync_AcceptsReferenceContractJson()
    {
        await using var dbContext = CreateDbContext();
        var service = CreateService(dbContext);

        var created = await service.CreateCustomAsync(
            "template-owner",
            "reference-contract-template",
            "Reference Contract Template",
            "Reference contract template.",
            "Custom",
            StrategyContractJson.Reference);

        Assert.Equal("reference-contract-template", created.TemplateKey);
        Assert.Equal("Valid", created.Validation.StatusCode);
        Assert.Equal(12, created.Validation.EnabledRuleCount);
        Assert.Contains("\"entry-root\"", created.DefinitionJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReviseAsync_AcceptsReferenceContractJson()
    {
        await using var dbContext = CreateDbContext();
        var service = CreateService(dbContext);
        _ = await service.CreateCustomAsync(
            "template-owner",
            "reference-revise-template",
            "Reference Revise Template",
            "Initial template.",
            "Custom",
            CreateTemplateDefinitionJson());

        var revised = await service.ReviseAsync(
            "reference-revise-template",
            "Reference Revise Template",
            "Reference contract revision.",
            "Custom",
            StrategyContractJson.Reference);

        Assert.Equal("Valid", revised.Validation.StatusCode);
        Assert.Equal(12, revised.Validation.EnabledRuleCount);
        Assert.Equal(2, revised.ActiveRevisionNumber);
    }

    [Fact]
    public async Task CreateCustomAsync_RejectsInvalidRootProperty_FailClosed()
    {
        await using var dbContext = CreateDbContext();
        var service = CreateService(dbContext);
        var invalidJson = StrategyContractJson.Reference.Replace("\"entry\": {", "\"signals\": {},\n  \"entry\": {", StringComparison.Ordinal);

        var exception = await Assert.ThrowsAsync<StrategyRuleParseException>(() => service.CreateCustomAsync(
            "template-owner",
            "invalid-root-template",
            "Invalid Root Template",
            "Invalid root template.",
            "Custom",
            invalidJson));

        Assert.Contains("strategy definition.signals", exception.Message, StringComparison.Ordinal);
        Assert.Empty(await dbContext.TradingStrategyTemplates.ToListAsync());
    }
    [Fact]
    public async Task CloneAsync_CreatesCustomTemplate_WithSourceTemplateMetadata()
    {
        await using var dbContext = CreateDbContext();
        var service = CreateService(dbContext);

        var cloned = await service.CloneAsync(
            "template-owner",
            "rsi-reversal",
            "rsi-reversal-clone",
            "RSI Reversal Clone",
            "Cloned built-in template.",
            "Clone");

        Assert.Equal("rsi-reversal-clone", cloned.TemplateKey);
        Assert.Equal("RSI Reversal Clone", cloned.TemplateName);
        Assert.Equal("rsi-reversal", cloned.SourceTemplateKey);
        Assert.Equal(1, cloned.SourceRevisionNumber);
        Assert.False(cloned.IsBuiltIn);
        Assert.Contains("\"templateKey\": \"rsi-reversal-clone\"", cloned.DefinitionJson, StringComparison.Ordinal);
    }
    [Fact]
    public async Task CreateCustomAsync_RejectsDuplicateTemplateKey_FailClosed()
    {
        await using var dbContext = CreateDbContext();
        var service = CreateService(dbContext);

        _ = await service.CreateCustomAsync(
            "template-owner",
            "duplicate-template",
            "Duplicate Template",
            "Initial template.",
            "Custom",
            CreateTemplateDefinitionJson());

        var exception = await Assert.ThrowsAsync<StrategyTemplateCatalogException>(() => service.CreateCustomAsync(
            "template-owner",
            "duplicate-template",
            "Duplicate Template 2",
            "Duplicate template.",
            "Custom",
            CreateTemplateDefinitionJson()));

        Assert.Equal("TemplateKeyAlreadyExists", exception.FailureCode);
        Assert.Contains("already exists", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ListAllAsync_AndGetIncludingArchivedAsync_ExposeArchivedTemplate_ForAdminCatalog()
    {
        await using var dbContext = CreateDbContext();
        var service = CreateService(dbContext);
        _ = await service.CreateCustomAsync(
            "template-owner",
            "admin-archived-template",
            "Admin Archived Template",
            "Archived custom template.",
            "Custom",
            CreateTemplateDefinitionJson());

        var archived = await service.ArchiveAsync("admin-archived-template");
        var listedTemplates = await service.ListAllAsync();
        var selected = await service.GetIncludingArchivedAsync("admin-archived-template");

        Assert.False(archived.IsActive);
        Assert.Contains(listedTemplates, template => template.TemplateKey == "admin-archived-template" && template.IsActive is false);
        Assert.False(selected.IsActive);
        Assert.NotNull(selected.ArchivedAtUtc);
    }

    [Fact]
    public async Task ArchiveAsync_HidesCustomTemplateFromActiveCatalog()
    {
        await using var dbContext = CreateDbContext();
        var service = CreateService(dbContext);
        _ = await service.CreateCustomAsync(
            "template-owner",
            "archivable-template",
            "Archivable Template",
            "Archivable custom template.",
            "Custom",
            CreateTemplateDefinitionJson());

        var archived = await service.ArchiveAsync("archivable-template");
        var listedTemplates = await service.ListAsync();

        Assert.False(archived.IsActive);
        Assert.DoesNotContain(listedTemplates, template => template.TemplateKey == "archivable-template");
        var archivedException = await Assert.ThrowsAsync<StrategyTemplateCatalogException>(() => service.GetAsync("archivable-template"));
        Assert.Equal("TemplateArchived", archivedException.FailureCode);
    }

    [Fact]
    public async Task ReviseAsync_CreatesImmutableRevisionHistory_AndUpdatesActiveRevision()
    {
        await using var dbContext = CreateDbContext();
        var service = CreateService(dbContext);

        _ = await service.CreateCustomAsync(
            "template-owner",
            "revisioned-template",
            "Revisioned Template",
            "Initial revision.",
            "Custom",
            CreateTemplateDefinitionJson());

        var revised = await service.ReviseAsync(
            "revisioned-template",
            "Revisioned Template v2",
            "Updated revision.",
            "Custom",
            CreateTemplateDefinitionJson(timeframe: "30m", latencyRule: true));
        var revisions = await service.ListRevisionsAsync("revisioned-template");

        Assert.Equal("Revisioned Template v2", revised.TemplateName);
        Assert.Equal(2, revised.ActiveRevisionNumber);
        Assert.Equal(2, revised.LatestRevisionNumber);
        Assert.Equal(2, revisions.Count);
        Assert.Contains(revisions, revision => revision.RevisionNumber == 1 && revision.IsActive is false && revision.SourceTemplateKey is null);
        Assert.Contains(revisions, revision => revision.RevisionNumber == 2 && revision.IsActive && revision.SourceTemplateKey == "revisioned-template" && revision.SourceRevisionNumber == 1);
    }

    [Fact]
    public async Task PublishAsync_PublishesSelectedRevision_AndGetAsync_UsesPublishedRevision()
    {
        await using var dbContext = CreateDbContext();
        var service = CreateService(dbContext);

        _ = await service.CreateCustomAsync(
            "template-owner",
            "published-template",
            "Published Template",
            "Initial published revision.",
            "Custom",
            CreateTemplateDefinitionJson());
        _ = await service.ReviseAsync(
            "published-template",
            "Published Template v2",
            "Draft revision.",
            "Custom",
            CreateTemplateDefinitionJson(timeframe: "30m", latencyRule: true));

        var beforePublish = await service.GetAsync("published-template");
        var published = await service.PublishAsync("published-template", 2);
        var afterPublish = await service.GetAsync("published-template");
        var revisions = await service.ListRevisionsAsync("published-template");

        Assert.Equal(1, beforePublish.PublishedRevisionNumber);
        Assert.Contains("\"templateRevisionNumber\": 1", beforePublish.DefinitionJson, StringComparison.Ordinal);
        Assert.Equal(2, published.PublishedRevisionNumber);
        Assert.Contains("\"templateRevisionNumber\": 2", afterPublish.DefinitionJson, StringComparison.Ordinal);
        Assert.Contains(revisions, revision => revision.RevisionNumber == 2 && revision.IsPublished);
        Assert.Contains(revisions, revision => revision.RevisionNumber == 1 && !revision.IsPublished);
    }


    [Fact]
    public async Task GetAsync_FailsClosed_WhenPublishedRevisionPointerIsBroken()
    {
        await using var dbContext = CreateDbContext();
        var service = CreateService(dbContext);

        _ = await service.CreateCustomAsync(
            "template-owner",
            "broken-published-template",
            "Broken Published Template",
            "Template with corrupted published pointer.",
            "Custom",
            CreateTemplateDefinitionJson());
        _ = await service.ReviseAsync(
            "broken-published-template",
            "Broken Published Template v2",
            "Draft revision.",
            "Custom",
            CreateTemplateDefinitionJson(timeframe: "30m", latencyRule: true));

        var template = await dbContext.TradingStrategyTemplates
            .SingleAsync(entity => entity.TemplateKey == "broken-published-template");
        template.PublishedTradingStrategyTemplateRevisionId = Guid.NewGuid();
        await dbContext.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<StrategyTemplateCatalogException>(() => service.GetAsync("broken-published-template"));
        var listedTemplates = await service.ListAsync();
        var adminSnapshot = await service.GetIncludingArchivedAsync("broken-published-template");

        Assert.Equal("TemplateUnpublished", exception.FailureCode);
        Assert.DoesNotContain(listedTemplates, item => item.TemplateKey == "broken-published-template");
        Assert.Equal(0, adminSnapshot.PublishedRevisionNumber);
        Assert.Equal(2, adminSnapshot.ActiveRevisionNumber);
    }

    [Fact]
    public async Task PublishAsync_RejectsArchivedTemplate_FailClosed()
    {
        await using var dbContext = CreateDbContext();
        var service = CreateService(dbContext);

        _ = await service.CreateCustomAsync(
            "template-owner",
            "archived-publish-template",
            "Archived Publish Template",
            "Archive then publish should fail.",
            "Custom",
            CreateTemplateDefinitionJson());
        _ = await service.ArchiveAsync("archived-publish-template");

        var exception = await Assert.ThrowsAsync<StrategyTemplateCatalogException>(() => service.PublishAsync("archived-publish-template", 1));

        Assert.Equal("TemplateArchived", exception.FailureCode);
        Assert.Contains("archived", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
    [Fact]
    public async Task ReviseAsync_RejectsInvalidRevision_FailClosed()
    {
        await using var dbContext = CreateDbContext();
        var service = CreateService(dbContext);

        _ = await service.CreateCustomAsync(
            "template-owner",
            "invalid-revision-template",
            "Invalid Revision Template",
            "Initial revision.",
            "Custom",
            CreateTemplateDefinitionJson());

        var exception = await Assert.ThrowsAsync<StrategyDefinitionValidationException>(() => service.ReviseAsync(
            "invalid-revision-template",
            "Invalid Revision Template",
            "Broken revision.",
            "Custom",
            """
            {
              "schemaVersion": 2,
              "entry": {
                "path": "indicator.source",
                "comparison": "greaterThan",
                "value": "stream",
                "ruleId": "invalid-source-op",
                "ruleType": "data-quality",
                "timeframe": "30m",
                "weight": 10,
                "enabled": true
              }
            }
            """));

        var revisions = await service.ListRevisionsAsync("invalid-revision-template");

        Assert.Equal("UnsupportedComparisonForPath:entry:indicator.source:GreaterThan", exception.StatusCode);
        Assert.Single(revisions);
    }

    [Fact]
    public async Task CreateCustomAsync_RejectsInvalidDefinition_FailClosed()
    {
        await using var dbContext = CreateDbContext();
        var service = CreateService(dbContext);

        var exception = await Assert.ThrowsAsync<StrategyDefinitionValidationException>(() => service.CreateCustomAsync(
            "template-owner",
            "invalid-template",
            "Invalid Template",
            "Invalid custom template.",
            "Custom",
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
        Assert.Empty(await dbContext.TradingStrategyTemplates.ToListAsync());
    }

    [Fact]
    public async Task ListAsync_AndGetAsync_ExposePlatformAdminTemplatesAsSharedCatalog()
    {
        var databaseName = Guid.NewGuid().ToString("N");
        var databaseRoot = new InMemoryDatabaseRoot();

        await using (var adminContext = CreateDbContext("platform-admin", hasIsolationBypass: false, databaseName: databaseName, databaseRoot: databaseRoot))
        {
            adminContext.UserClaims.Add(new IdentityUserClaim<string>
            {
                UserId = "platform-admin",
                ClaimType = ApplicationClaimTypes.Permission,
                ClaimValue = ApplicationPermissions.PlatformAdministration
            });
            var adminService = CreateService(adminContext);
            _ = await adminService.CreateCustomAsync(
                "platform-admin",
                "shared-admin-template",
                "Shared Admin Template",
                "Shared admin template.",
                "Custom",
                CreateTemplateDefinitionJson());
        }

        await using (var otherContext = CreateDbContext("other-user", hasIsolationBypass: false, databaseName: databaseName, databaseRoot: databaseRoot))
        {
            var otherService = CreateService(otherContext);
            _ = await otherService.CreateCustomAsync(
                "other-user",
                "other-private-template",
                "Other Private Template",
                "Other private template.",
                "Custom",
                CreateTemplateDefinitionJson());
        }

        await using var userContext = CreateDbContext("normal-user", hasIsolationBypass: false, databaseName: databaseName, databaseRoot: databaseRoot);
        var userService = CreateService(userContext);

        var templates = await userService.ListAsync();
        var selected = await userService.GetAsync("shared-admin-template");

        Assert.Contains(templates, template => template.TemplateKey == "shared-admin-template" && template.TemplateSource == "Custom");
        Assert.Equal("shared-admin-template", selected.TemplateKey);
        Assert.DoesNotContain(templates, template => template.TemplateKey == "other-private-template");
        var exception = await Assert.ThrowsAsync<StrategyTemplateCatalogException>(() => userService.GetAsync("other-private-template"));
        Assert.Equal("TemplateNotFound", exception.FailureCode);
    }
    [Fact]
    public async Task ListAsync_AndGetAsync_DoNotLeakCustomTemplatesAcrossUserScope()
    {
        var databaseName = Guid.NewGuid().ToString("N");
        var databaseRoot = new InMemoryDatabaseRoot();

        await using (var ownerContext = CreateDbContext("template-owner-a", hasIsolationBypass: false, databaseName: databaseName, databaseRoot: databaseRoot))
        {
            var ownerService = CreateService(ownerContext);
            _ = await ownerService.CreateCustomAsync(
                "template-owner-a",
                "isolated-template",
                "Isolated Template",
                "Owner A only.",
                "Custom",
                CreateTemplateDefinitionJson());
        }

        await using var foreignContext = CreateDbContext("template-owner-b", hasIsolationBypass: false, databaseName: databaseName, databaseRoot: databaseRoot);
        var foreignService = CreateService(foreignContext);

        var templates = await foreignService.ListAsync();

        Assert.DoesNotContain(templates, template => template.TemplateKey == "isolated-template");
        var exception = await Assert.ThrowsAsync<StrategyTemplateCatalogException>(() => foreignService.GetAsync("isolated-template"));
        Assert.Equal("TemplateNotFound", exception.FailureCode);
    }

    [Fact]
    public async Task CreateCustomAsync_RejectsRequestedOwnerOutsideCurrentScope()
    {
        await using var dbContext = CreateDbContext("template-owner-a", hasIsolationBypass: false);
        var service = CreateService(dbContext);

        var exception = await Assert.ThrowsAsync<StrategyTemplateCatalogException>(() => service.CreateCustomAsync(
            "template-owner-b",
            "cross-scope-template",
            "Cross Scope Template",
            "Should fail.",
            "Custom",
            CreateTemplateDefinitionJson()));

        Assert.Equal("TemplateOwnershipScopeViolation", exception.FailureCode);
        Assert.Contains("outside the authenticated isolation boundary", exception.Message, StringComparison.Ordinal);
    }

    private static StrategyTemplateCatalogService CreateService(ApplicationDbContext dbContext)
    {
        return new StrategyTemplateCatalogService(
            new StrategyRuleParser(),
            new StrategyDefinitionValidator(),
            dbContext);
    }

    private static ApplicationDbContext CreateDbContext(
        string? userId = null,
        bool hasIsolationBypass = true,
        string? databaseName = null,
        InMemoryDatabaseRoot? databaseRoot = null)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName ?? Guid.NewGuid().ToString("N"), databaseRoot)
            .Options;

        return new ApplicationDbContext(options, new TestDataScopeContext(userId, hasIsolationBypass));
    }

    private static string CreateTemplateDefinitionJson(string timeframe = "1m", bool latencyRule = false)
    {
        var latencyClause = latencyRule
            ? $$"""
                  ,
                  {
                    "ruleId": "entry-latency",
                    "ruleType": "data-quality",
                    "path": "indicator.latencySeconds",
                    "comparison": "between",
                    "value": "0..5",
                    "timeframe": "{{timeframe}}",
                    "weight": 15,
                    "enabled": true
                  }
               """
            : string.Empty;

        return
            $$"""
            {
              "schemaVersion": 2,
              "entry": {
                "operator": "all",
                "ruleId": "entry-root",
                "ruleType": "group",
                "timeframe": "{{timeframe}}",
                "weight": 1,
                "enabled": true,
                "rules": [
                  {
                    "ruleId": "entry-mode",
                    "ruleType": "context",
                    "path": "context.mode",
                    "comparison": "equals",
                    "value": "Live",
                    "timeframe": "{{timeframe}}",
                    "weight": 20,
                    "enabled": true
                  },
                  {
                    "ruleId": "entry-rsi",
                    "ruleType": "rsi",
                    "path": "indicator.rsi.value",
                    "comparison": "lessThanOrEqual",
                    "value": 30,
                    "timeframe": "{{timeframe}}",
                    "weight": 80,
                    "enabled": true
                  }{{latencyClause}}
                ]
              }
            }
            """;
    }

    private static string? ResolveTimeframe(StrategyRuleNode node)
    {
        return node switch
        {
            StrategyRuleGroup group => group.Metadata?.Timeframe,
            StrategyRuleCondition condition => condition.Metadata?.Timeframe,
            _ => null
        };
    }

    private sealed class TestDataScopeContext(string? userId, bool hasIsolationBypass) : IDataScopeContext
    {
        public string? UserId => userId;

        public bool HasIsolationBypass => hasIsolationBypass;
    }
}




