using System.Security.Claims;
using CoinBot.Application.Abstractions.DataScope;
using CoinBot.Application.Abstractions.Strategies;
using CoinBot.Contracts.Common;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Jobs;
using CoinBot.Infrastructure.Persistence;
using CoinBot.Infrastructure.Strategies;
using CoinBot.Web.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CoinBot.UnitTests.Web;

public sealed class StrategyBuilderControllerTests
{
    [Fact]
    public async Task Index_ReturnsPublishedTemplates_AndOwnedTargetStrategies()
    {
        await using var dbContext = CreateDbContext();
        dbContext.TradingStrategies.AddRange(
            new TradingStrategy
            {
                Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                OwnerUserId = "builder-user",
                StrategyKey = "alpha-core",
                DisplayName = "Alpha Core"
            },
            new TradingStrategy
            {
                Id = Guid.Parse("22222222-2222-2222-2222-222222222222"),
                OwnerUserId = "builder-user",
                StrategyKey = "beta-core",
                DisplayName = ""
            },
            new TradingStrategy
            {
                Id = Guid.Parse("33333333-3333-3333-3333-333333333333"),
                OwnerUserId = "other-user",
                StrategyKey = "gamma-core",
                DisplayName = "Gamma Core"
            });
        await dbContext.SaveChangesAsync();

        var templateCatalog = new FakeStrategyTemplateCatalogService();
        var controller = CreateController(dbContext, templateCatalog, new FakeStrategyVersionService(), "builder-user");

        var result = await controller.Index(CancellationToken.None);

        var viewResult = Assert.IsType<ViewResult>(result);
        var templates = Assert.IsAssignableFrom<IReadOnlyCollection<StrategyTemplateSnapshot>>(controller.ViewData["StrategyTemplateCatalog"]);
        var targets = Assert.IsAssignableFrom<IReadOnlyCollection<SelectListItem>>(controller.ViewData["StrategyBuilderDraftTargets"]);
        var runtimeConfigJson = Assert.IsType<string>(controller.ViewData["StrategyBuilderRuntimeConfigJson"]);

        Assert.Same(templates, templateCatalog.ListResult);
        Assert.Equal(2, targets.Count);
        Assert.Contains(targets, item => item.Text == "Alpha Core (alpha-core)");
        Assert.Contains(targets, item => item.Text == "beta-core");
        Assert.DoesNotContain(targets, item => item.Text.Contains("gamma-core", StringComparison.Ordinal));
        Assert.Contains("Long", runtimeConfigJson, StringComparison.Ordinal);
        Assert.Contains("Short", runtimeConfigJson, StringComparison.Ordinal);
    }


    [Fact]
    public async Task CreateDraftFromTemplate_BlocksEmptyTemplateSelection_FailClosed()
    {
        await using var dbContext = CreateDbContext();
        var strategyId = Guid.Parse("12121212-1212-1212-1212-121212121212");
        dbContext.TradingStrategies.Add(new TradingStrategy
        {
            Id = strategyId,
            OwnerUserId = "builder-user",
            StrategyKey = "empty-template-core",
            DisplayName = "Empty Template Core"
        });
        await dbContext.SaveChangesAsync();

        var versionService = new FakeStrategyVersionService();
        var controller = CreateController(dbContext, new FakeStrategyTemplateCatalogService(), versionService, "builder-user");

        var result = await controller.CreateDraftFromTemplate(strategyId, "   ", CancellationToken.None);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(StrategyBuilderController.Index), redirect.ActionName);
        Assert.Empty(versionService.CreateDraftFromTemplateRequests);
        Assert.Equal("Template veya builder kaydi gerekli.", controller.TempData["StrategyBuilderTemplateError"]?.ToString());
    }

    [Fact]
    public async Task CreateDraftFromTemplate_CreatesIndependentDraft_AndSetsSuccessFeedback()
    {
        await using var dbContext = CreateDbContext();
        var strategyId = Guid.Parse("44444444-4444-4444-4444-444444444444");
        dbContext.TradingStrategies.Add(new TradingStrategy
        {
            Id = strategyId,
            OwnerUserId = "builder-user",
            StrategyKey = "clone-core",
            DisplayName = "Clone Core"
        });
        await dbContext.SaveChangesAsync();

        var versionService = new FakeStrategyVersionService
        {
            NextDraft = new StrategyVersionSnapshot(
                Guid.Parse("55555555-5555-5555-5555-555555555555"),
                strategyId,
                2,
                3,
                StrategyVersionStatus.Draft,
                null,
                null,
                TemplateKey: "rsi-reversal",
                TemplateName: "RSI Reversal",
                ValidationStatusCode: "Valid",
                ValidationSummary: "Ready",
                TemplateRevisionNumber: 2,
                TemplateSource: "BuiltIn")
        };
        var controller = CreateController(dbContext, new FakeStrategyTemplateCatalogService(), versionService, "builder-user");

        var result = await controller.CreateDraftFromTemplate(strategyId, "rsi-reversal", CancellationToken.None);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(StrategyBuilderController.Index), redirect.ActionName);
        var request = Assert.Single(versionService.CreateDraftFromTemplateRequests);
        Assert.Equal(strategyId, request.StrategyId);
        Assert.Equal("rsi-reversal", request.TemplateKey);
        Assert.Equal("Strateji güncellendi. Bot ekranında seçebilirsiniz.", controller.TempData["StrategyBuilderTemplateSuccess"]?.ToString());
        Assert.Contains(versionService.PublishRequests, requestId => requestId == versionService.LastDraftId);
    }

    [Fact]
    public async Task CreateDraftFromTemplate_BlocksOtherUsersStrategy_FailClosed()
    {
        await using var dbContext = CreateDbContext();
        var strategyId = Guid.Parse("66666666-6666-6666-6666-666666666666");
        dbContext.TradingStrategies.Add(new TradingStrategy
        {
            Id = strategyId,
            OwnerUserId = "other-user",
            StrategyKey = "foreign-core",
            DisplayName = "Foreign Core"
        });
        await dbContext.SaveChangesAsync();

        var versionService = new FakeStrategyVersionService();
        var controller = CreateController(dbContext, new FakeStrategyTemplateCatalogService(), versionService, "builder-user");

        var result = await controller.CreateDraftFromTemplate(strategyId, "rsi-reversal", CancellationToken.None);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(StrategyBuilderController.Index), redirect.ActionName);
        Assert.Empty(versionService.CreateDraftFromTemplateRequests);
        Assert.Equal("Strateji bulunamadı.", controller.TempData["StrategyBuilderTemplateError"]?.ToString());
    }

    [Fact]
    public async Task CreateDraftFromTemplate_BlocksArchivedTemplate_AndShowsSanitizedFeedback()
    {
        await using var dbContext = CreateDbContext();
        var strategyId = Guid.Parse("77777777-7777-7777-7777-777777777777");
        dbContext.TradingStrategies.Add(new TradingStrategy
        {
            Id = strategyId,
            OwnerUserId = "builder-user",
            StrategyKey = "archive-core",
            DisplayName = "Archive Core"
        });
        await dbContext.SaveChangesAsync();

        var versionService = new FakeStrategyVersionService
        {
            NextException = new StrategyTemplateCatalogException("TemplateArchived", "Strategy template 'archived-template' is archived and cannot be used for clone or publish flows.")
        };
        var controller = CreateController(dbContext, new FakeStrategyTemplateCatalogService(), versionService, "builder-user");

        _ = await controller.CreateDraftFromTemplate(strategyId, "archived-template", CancellationToken.None);

        Assert.Equal("Template kullanılamıyor.", controller.TempData["StrategyBuilderTemplateError"]?.ToString());
    }

    [Fact]
    public async Task CreateDraftFromTemplate_BlocksInvalidTemplateKey_AndShowsSanitizedFeedback()
    {
        await using var dbContext = CreateDbContext();
        var strategyId = Guid.Parse("88888888-8888-8888-8888-888888888888");
        dbContext.TradingStrategies.Add(new TradingStrategy
        {
            Id = strategyId,
            OwnerUserId = "builder-user",
            StrategyKey = "invalid-core",
            DisplayName = "Invalid Core"
        });
        await dbContext.SaveChangesAsync();

        var versionService = new FakeStrategyVersionService
        {
            NextException = new StrategyTemplateCatalogException("TemplateNotFound", "Strategy template 'missing-template' was not found.")
        };
        var controller = CreateController(dbContext, new FakeStrategyTemplateCatalogService(), versionService, "builder-user");

        _ = await controller.CreateDraftFromTemplate(strategyId, "missing-template", CancellationToken.None);

        Assert.Equal("Template kullanılamıyor.", controller.TempData["StrategyBuilderTemplateError"]?.ToString());
    }

    [Fact]
    public async Task CreateDraftFromTemplate_SanitizesValidationFailureFeedback()
    {
        await using var dbContext = CreateDbContext();
        var strategyId = Guid.Parse("99999999-9999-9999-9999-999999999999");
        dbContext.TradingStrategies.Add(new TradingStrategy
        {
            Id = strategyId,
            OwnerUserId = "builder-user",
            StrategyKey = "validation-core",
            DisplayName = "Validation Core"
        });
        await dbContext.SaveChangesAsync();

        var versionService = new FakeStrategyVersionService
        {
            NextException = new StrategyDefinitionValidationException(
                "InvalidRule",
                "Validation failed for strategy template payload.",
                ["ReasonA", "ReasonB", "ReasonC"])
        };
        var controller = CreateController(dbContext, new FakeStrategyTemplateCatalogService(), versionService, "builder-user");

        _ = await controller.CreateDraftFromTemplate(strategyId, "invalid-template", CancellationToken.None);

        Assert.Equal("Strateji taslağı oluşturulamadı.", controller.TempData["StrategyBuilderTemplateError"]?.ToString());
    }

    [Fact]
    public async Task CreateDraftFromTemplate_SanitizesParseFailureFeedback()
    {
        await using var dbContext = CreateDbContext();
        var strategyId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        dbContext.TradingStrategies.Add(new TradingStrategy
        {
            Id = strategyId,
            OwnerUserId = "builder-user",
            StrategyKey = "parse-core",
            DisplayName = "Parse Core"
        });
        await dbContext.SaveChangesAsync();

        var versionService = new FakeStrategyVersionService
        {
            NextException = new StrategyRuleParseException(new string('x', 400))
        };
        var controller = CreateController(dbContext, new FakeStrategyTemplateCatalogService(), versionService, "builder-user");

        _ = await controller.CreateDraftFromTemplate(strategyId, "parse-template", CancellationToken.None);

        Assert.Equal("Strateji taslağı oluşturulamadı.", controller.TempData["StrategyBuilderTemplateError"]?.ToString());
    }

    [Fact]
    public async Task CreateDraftFromTemplate_WithBuilderDefinition_UsesCreateDraftAsync_AndPublishes()
    {
        await using var dbContext = CreateDbContext();
        var strategyId = Guid.Parse("abababab-abab-abab-abab-abababababab");
        dbContext.TradingStrategies.Add(new TradingStrategy
        {
            Id = strategyId,
            OwnerUserId = "builder-user",
            StrategyKey = "builder-definition-core",
            DisplayName = "Builder Definition Core"
        });
        await dbContext.SaveChangesAsync();

        var versionService = new FakeStrategyVersionService();
        var controller = CreateController(dbContext, new FakeStrategyTemplateCatalogService(), versionService, "builder-user");
        SetPostedDefinitionJson(controller, "{\"schemaVersion\":2,\"entry\":{\"path\":\"indicator.rsi.value\",\"comparison\":\"lessThanOrEqual\",\"value\":30,\"ruleId\":\"entry-rsi\",\"ruleType\":\"rsi\",\"weight\":10,\"enabled\":true}}");

        var result = await controller.CreateDraftFromTemplate(strategyId, "rsi-reversal", CancellationToken.None);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(StrategyBuilderController.Index), redirect.ActionName);
        var request = Assert.Single(versionService.CreateDraftRequests);
        Assert.Equal(strategyId, request.StrategyId);
        Assert.Contains("\"schemaVersion\":2", request.DefinitionJson, StringComparison.Ordinal);
        Assert.Empty(versionService.CreateDraftFromTemplateRequests);
        Assert.Contains(versionService.PublishRequests, requestId => requestId == versionService.LastDraftId);
    }

    [Fact]
    public async Task StartFromTemplate_WithBuilderDefinition_CreatesOwnedStrategy_AndUsesCreateDraftAsync()
    {
        await using var dbContext = CreateDbContext();
        var templateCatalog = new FakeStrategyTemplateCatalogService();
        var versionService = new FakeStrategyVersionService();
        var controller = CreateController(dbContext, templateCatalog, versionService, "builder-user");
        SetPostedDefinitionJson(controller, "{\"schemaVersion\":2,\"entry\":{\"path\":\"indicator.rsi.value\",\"comparison\":\"lessThanOrEqual\",\"value\":30,\"ruleId\":\"entry-rsi\",\"ruleType\":\"rsi\",\"weight\":10,\"enabled\":true}}");

        var result = await controller.StartFromTemplate("rsi-reversal", "BTC Builder", CancellationToken.None);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(StrategyBuilderController.Index), redirect.ActionName);
        var strategy = await dbContext.TradingStrategies.SingleAsync();
        var request = Assert.Single(versionService.CreateDraftRequests);
        Assert.Equal(strategy.Id, request.StrategyId);
        Assert.Empty(versionService.CreateDraftFromTemplateRequests);
        Assert.Equal("BTC Builder", strategy.DisplayName);
        Assert.Contains(versionService.PublishRequests, requestId => requestId == versionService.LastDraftId);
    }

    [Fact]
    public async Task CreateDraftFromTemplate_MapsInvalidThresholdValidation_ToSafeMessage()
    {
        await using var dbContext = CreateDbContext();
        var strategyId = Guid.Parse("cdcdcdcd-cdcd-cdcd-cdcd-cdcdcdcdcdcd");
        dbContext.TradingStrategies.Add(new TradingStrategy
        {
            Id = strategyId,
            OwnerUserId = "builder-user",
            StrategyKey = "threshold-core",
            DisplayName = "Threshold Core"
        });
        await dbContext.SaveChangesAsync();

        var versionService = new FakeStrategyVersionService
        {
            NextException = new StrategyDefinitionValidationException(
                "InvalidRsiThreshold:entry:101",
                "Validation failed.",
                ["InvalidRsiThreshold:entry:101"])
        };
        var controller = CreateController(dbContext, new FakeStrategyTemplateCatalogService(), versionService, "builder-user");
        SetPostedDefinitionJson(controller, "{\"schemaVersion\":2}");

        _ = await controller.CreateDraftFromTemplate(strategyId, "rsi-reversal", CancellationToken.None);

        Assert.Equal("Kaydetme engellendi: eşik veya aralık değeri geçersiz.", controller.TempData["StrategyBuilderTemplateError"]?.ToString());
    }

    [Fact]
    public async Task CreateDraftFromTemplate_MapsConflictingRuleValidation_ToSafeMessage()
    {
        await using var dbContext = CreateDbContext();
        var strategyId = Guid.Parse("edededed-eded-eded-eded-edededededed");
        dbContext.TradingStrategies.Add(new TradingStrategy
        {
            Id = strategyId,
            OwnerUserId = "builder-user",
            StrategyKey = "conflict-core",
            DisplayName = "Conflict Core"
        });
        await dbContext.SaveChangesAsync();

        var versionService = new FakeStrategyVersionService
        {
            NextException = new StrategyDefinitionValidationException(
                "ConflictingRule:entry.rules[1]:context.mode",
                "Validation failed.",
                ["ConflictingRule:entry.rules[1]:context.mode"])
        };
        var controller = CreateController(dbContext, new FakeStrategyTemplateCatalogService(), versionService, "builder-user");
        SetPostedDefinitionJson(controller, "{\"schemaVersion\":2}");

        _ = await controller.CreateDraftFromTemplate(strategyId, "rsi-reversal", CancellationToken.None);

        Assert.Equal("Kaydetme engellendi: çelişkili veya tekrar eden kural var.", controller.TempData["StrategyBuilderTemplateError"]?.ToString());
    }

    [Fact]
    public async Task CreateDraftFromTemplate_MapsMissingFieldParse_ToSafeMessage()
    {
        await using var dbContext = CreateDbContext();
        var strategyId = Guid.Parse("efefefef-efef-efef-efef-efefefefefef");
        dbContext.TradingStrategies.Add(new TradingStrategy
        {
            Id = strategyId,
            OwnerUserId = "builder-user",
            StrategyKey = "missing-core",
            DisplayName = "Missing Core"
        });
        await dbContext.SaveChangesAsync();

        var versionService = new FakeStrategyVersionService
        {
            NextException = new StrategyRuleParseException("Strategy rule property 'entry.path' is required.")
        };
        var controller = CreateController(dbContext, new FakeStrategyTemplateCatalogService(), versionService, "builder-user");
        SetPostedDefinitionJson(controller, "{\"schemaVersion\":2}");

        _ = await controller.CreateDraftFromTemplate(strategyId, "rsi-reversal", CancellationToken.None);

        Assert.Equal("Kaydetme engellendi: builder alanlarında eksik veya hatalı veri var.", controller.TempData["StrategyBuilderTemplateError"]?.ToString());
    }

    [Fact]
    public async Task StartFromTemplate_CreatesOwnedStrategy_AndDraftFromTemplate()
    {
        await using var dbContext = CreateDbContext();
        var templateCatalog = new FakeStrategyTemplateCatalogService();
        var versionService = new FakeStrategyVersionService();
        var controller = CreateController(dbContext, templateCatalog, versionService, "builder-user");

        var result = await controller.StartFromTemplate("rsi-reversal", "BTC Ana", CancellationToken.None);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(StrategyBuilderController.Index), redirect.ActionName);
        var strategy = await dbContext.TradingStrategies.SingleAsync();
        Assert.Equal("builder-user", strategy.OwnerUserId);
        Assert.Equal("BTC Ana", strategy.DisplayName);
        Assert.Equal("btc-ana", strategy.StrategyKey);
        var request = Assert.Single(versionService.CreateDraftFromTemplateRequests);
        Assert.Equal(strategy.Id, request.StrategyId);
        Assert.Equal("rsi-reversal", request.TemplateKey);
        Assert.Equal("Strateji oluşturuldu. Bot oluştururken bu stratejiyi seçebilirsiniz.", controller.TempData["StrategyBuilderTemplateSuccess"]?.ToString());
        Assert.Contains(versionService.PublishRequests, requestId => requestId == versionService.LastDraftId);
    }

    [Fact]
    public async Task CreateDraftFromTemplate_WithBuilderDefinition_EndToEnd_PublishesCanonicalVersion_AndReloadsToSameFormState()
    {
        await using var dbContext = CreateDbContext();
        var strategyId = Guid.Parse("10101010-2020-3030-4040-505050505050");
        dbContext.TradingStrategies.Add(new TradingStrategy
        {
            Id = strategyId,
            OwnerUserId = "builder-user",
            StrategyKey = "builder-e2e-core",
            DisplayName = "Builder E2E Core"
        });
        await dbContext.SaveChangesAsync();

        var controller = CreateController(
            dbContext,
            new FakeStrategyTemplateCatalogService(),
            CreateRealStrategyVersionService(dbContext),
            "builder-user");
        var postedDefinitionJson = CreateBuilderDefinitionJson();
        SetPostedDefinitionJson(controller, postedDefinitionJson);

        var result = await controller.CreateDraftFromTemplate(strategyId, string.Empty, CancellationToken.None);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(StrategyBuilderController.Index), redirect.ActionName);
        Assert.Equal("Strateji güncellendi. Bot ekranında seçebilirsiniz.", controller.TempData["StrategyBuilderTemplateSuccess"]?.ToString());

        var persistedVersion = await dbContext.TradingStrategyVersions.SingleAsync();
        var strategy = await dbContext.TradingStrategies.SingleAsync(entity => entity.Id == strategyId);
        var expectedCanonicalJson = Canonicalize(postedDefinitionJson);

        Assert.Equal(StrategyVersionStatus.Published, persistedVersion.Status);
        Assert.Equal(expectedCanonicalJson, persistedVersion.DefinitionJson);
        Assert.Equal(persistedVersion.Id, strategy.ActiveTradingStrategyVersionId);
        Assert.True(strategy.UsesExplicitVersionLifecycle);
        Assert.NotNull(strategy.ActiveVersionActivatedAtUtc);
        AssertDefinitionRoundTripsToSameFormState(postedDefinitionJson, persistedVersion.DefinitionJson);
    }

    [Fact]
    public async Task StartFromTemplate_WithBuilderDefinition_EndToEnd_CreatesOwnedStrategy_AndReloadsPublishedCanonicalJson()
    {
        await using var dbContext = CreateDbContext();
        var controller = CreateController(
            dbContext,
            new FakeStrategyTemplateCatalogService(),
            CreateRealStrategyVersionService(dbContext),
            "builder-user");
        var postedDefinitionJson = CreateBuilderDefinitionJson();
        SetPostedDefinitionJson(controller, postedDefinitionJson);

        var result = await controller.StartFromTemplate(string.Empty, "Builder Save Flow", CancellationToken.None);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(StrategyBuilderController.Index), redirect.ActionName);
        Assert.Equal("Strateji oluşturuldu. Bot oluştururken bu stratejiyi seçebilirsiniz.", controller.TempData["StrategyBuilderTemplateSuccess"]?.ToString());

        var strategy = await dbContext.TradingStrategies.SingleAsync();
        var persistedVersion = await dbContext.TradingStrategyVersions.SingleAsync(entity => entity.TradingStrategyId == strategy.Id);

        Assert.Equal("builder-user", strategy.OwnerUserId);
        Assert.Equal("Builder Save Flow", strategy.DisplayName);
        Assert.Equal("builder-save-flow", strategy.StrategyKey);
        Assert.Equal(StrategyVersionStatus.Published, persistedVersion.Status);
        Assert.Equal(persistedVersion.Id, strategy.ActiveTradingStrategyVersionId);
        AssertDefinitionRoundTripsToSameFormState(postedDefinitionJson, persistedVersion.DefinitionJson);
    }

    [Fact]
    public async Task CreateDraftFromTemplate_WithInvalidBuilderDefinition_EndToEnd_DoesNotPersistOrPublishVersion()
    {
        await using var dbContext = CreateDbContext();
        var strategyId = Guid.Parse("60606060-7070-8080-9090-a0a0a0a0a0a0");
        dbContext.TradingStrategies.Add(new TradingStrategy
        {
            Id = strategyId,
            OwnerUserId = "builder-user",
            StrategyKey = "builder-invalid-core",
            DisplayName = "Builder Invalid Core"
        });
        await dbContext.SaveChangesAsync();

        var controller = CreateController(
            dbContext,
            new FakeStrategyTemplateCatalogService(),
            CreateRealStrategyVersionService(dbContext),
            "builder-user");
        SetPostedDefinitionJson(
            controller,
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
            """);

        var result = await controller.CreateDraftFromTemplate(strategyId, string.Empty, CancellationToken.None);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(StrategyBuilderController.Index), redirect.ActionName);
        Assert.Equal("Kaydetme engellendi: eşik veya aralık değeri geçersiz.", controller.TempData["StrategyBuilderTemplateError"]?.ToString());
        Assert.Empty(await dbContext.TradingStrategyVersions.ToListAsync());

        var strategy = await dbContext.TradingStrategies.SingleAsync(entity => entity.Id == strategyId);
        Assert.Null(strategy.ActiveTradingStrategyVersionId);
        Assert.False(strategy.UsesExplicitVersionLifecycle);
    }

    [Fact]
    public void Controller_RequiresTradeOperations_AndClonePostRequiresAntiForgery()
    {
        var authorizeAttribute = Assert.Single(
            typeof(StrategyBuilderController)
                .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
                .Cast<AuthorizeAttribute>());
        var cloneAntiForgeryAttribute = Assert.Single(
            typeof(StrategyBuilderController)
                .GetMethod(nameof(StrategyBuilderController.CreateDraftFromTemplate), [typeof(Guid), typeof(string), typeof(CancellationToken)])!
                .GetCustomAttributes(typeof(ValidateAntiForgeryTokenAttribute), inherit: true)
                .Cast<ValidateAntiForgeryTokenAttribute>());
        var startAntiForgeryAttribute = Assert.Single(
            typeof(StrategyBuilderController)
                .GetMethod(nameof(StrategyBuilderController.StartFromTemplate), [typeof(string), typeof(string), typeof(CancellationToken)])!
                .GetCustomAttributes(typeof(ValidateAntiForgeryTokenAttribute), inherit: true)
                .Cast<ValidateAntiForgeryTokenAttribute>());

        Assert.Equal(ApplicationPolicies.TradeOperations, authorizeAttribute.Policy);
        Assert.NotNull(cloneAntiForgeryAttribute);
        Assert.NotNull(startAntiForgeryAttribute);
    }

    private static StrategyBuilderController CreateController(
        ApplicationDbContext dbContext,
        IStrategyTemplateCatalogService templateCatalogService,
        IStrategyVersionService strategyVersionService,
        string userId)
    {
        var controller = new StrategyBuilderController(
            templateCatalogService,
            strategyVersionService,
            dbContext,
            Options.Create(new BotExecutionPilotOptions
            {
                LongRegimeFilterEnabled = true,
                ShortRegimeFilterEnabled = true,
                LongRegimeMaxEntryRsi = 68m,
                ShortRegimeMinEntryRsi = 32m,
                LongRegimeMinMacdHistogram = -0.005m,
                ShortRegimeMaxMacdHistogram = 0.005m,
                LongRegimeMinBollingerWidthPercentage = 0.07m,
                ShortRegimeMinBollingerWidthPercentage = 0.09m
            }));
        var httpContext = new DefaultHttpContext();
        httpContext.User = new ClaimsPrincipal(
            new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, userId)],
                "TestAuth"));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext
        };
        controller.TempData = new TempDataDictionary(httpContext, new TestTempDataProvider());
        return controller;
    }

    private static void SetPostedDefinitionJson(StrategyBuilderController controller, string definitionJson)
    {
        controller.ControllerContext.HttpContext.Request.ContentType = "application/x-www-form-urlencoded";
        controller.ControllerContext.HttpContext.Features.Set<IFormFeature>(
            new FormFeature(new FormCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>(StringComparer.Ordinal)
            {
                ["definitionJson"] = definitionJson
            })));
    }


    private static StrategyVersionService CreateRealStrategyVersionService(ApplicationDbContext dbContext)
    {
        var parser = new StrategyRuleParser();
        var validator = new StrategyDefinitionValidator();
        return new StrategyVersionService(dbContext, parser, TimeProvider.System, validator, new FakeStrategyTemplateCatalogService());
    }

    private static string CreateBuilderDefinitionJson()
    {
        return
            """
            {
              "entry": {
                "rules": [
                  {
                    "comparison": "lessThanOrEqual",
                    "value": 30.000,
                    "path": "indicator.rsi.value",
                    "ruleType": "rsi",
                    "ruleId": "entry-rsi",
                    "enabled": true,
                    "weight": 10.000,
                    "group": "entry",
                    "timeframe": "1m"
                  },
                  {
                    "comparison": "equals",
                    "value": "Live",
                    "path": "context.mode",
                    "ruleType": "context",
                    "ruleId": "entry-mode",
                    "enabled": true,
                    "weight": 5.000,
                    "group": "entry",
                    "timeframe": "1m"
                  }
                ],
                "enabled": true,
                "weight": 1.000,
                "timeframe": "1m",
                "ruleType": "group",
                "ruleId": "entry-root",
                "operator": "all",
                "group": "entry"
              },
              "metadata": {
                "templateName": " Builder Save Flow ",
                "templateKey": " builder-save-flow ",
                "templateSource": " Custom "
              },
              "schemaVersion": 2
            }
            """;
    }

    private static string Canonicalize(string definitionJson)
    {
        return StrategyDefinitionCanonicalJsonSerializer.Serialize(new StrategyRuleParser().Parse(definitionJson));
    }

    private static void AssertDefinitionRoundTripsToSameFormState(string postedDefinitionJson, string reloadedDefinitionJson)
    {
        var postedCanonicalJson = Canonicalize(postedDefinitionJson);
        var reloadedCanonicalJson = Canonicalize(reloadedDefinitionJson);
        Assert.Equal(postedCanonicalJson, reloadedCanonicalJson);

        var postedDocument = new StrategyRuleParser().Parse(postedCanonicalJson);
        var reloadedDocument = new StrategyRuleParser().Parse(reloadedCanonicalJson);

        Assert.Equal(postedDocument.SchemaVersion, reloadedDocument.SchemaVersion);
        Assert.Equal(postedDocument.Metadata?.TemplateKey, reloadedDocument.Metadata?.TemplateKey);
        Assert.Equal(postedDocument.Metadata?.TemplateName, reloadedDocument.Metadata?.TemplateName);
        Assert.Equal(postedDocument.Metadata?.TemplateSource, reloadedDocument.Metadata?.TemplateSource);
        Assert.Equal(FlattenRuleFingerprints(postedDocument), FlattenRuleFingerprints(reloadedDocument));
    }

    private static IReadOnlyList<string> FlattenRuleFingerprints(StrategyRuleDocument document)
    {
        var fingerprints = new List<string>();
        AppendFingerprints(fingerprints, "entry", document.Entry);
        AppendFingerprints(fingerprints, "exit", document.Exit);
        AppendFingerprints(fingerprints, "risk", document.Risk);
        AppendFingerprints(fingerprints, "longEntry", document.LongEntry);
        AppendFingerprints(fingerprints, "longExit", document.LongExit);
        AppendFingerprints(fingerprints, "shortEntry", document.ShortEntry);
        AppendFingerprints(fingerprints, "shortExit", document.ShortExit);
        return fingerprints;
    }

    private static void AppendFingerprints(List<string> fingerprints, string rootName, StrategyRuleNode? node)
    {
        if (node is null)
        {
            return;
        }

        switch (node)
        {
            case StrategyRuleGroup group:
                fingerprints.Add($"{rootName}|group|{group.Metadata?.RuleId}|{group.Metadata?.RuleType}|{group.Operator}|{group.Metadata?.Timeframe}|{group.Metadata?.Weight}|{group.Metadata?.Enabled}|{group.Metadata?.Group}");
                foreach (var child in group.Rules)
                {
                    AppendFingerprints(fingerprints, rootName, child);
                }
                break;
            case StrategyRuleCondition condition:
                fingerprints.Add($"{rootName}|condition|{condition.Metadata?.RuleId}|{condition.Metadata?.RuleType}|{condition.Path}|{condition.Comparison}|{condition.Operand.Kind}|{condition.Operand.Value}|{condition.Metadata?.Timeframe}|{condition.Metadata?.Weight}|{condition.Metadata?.Enabled}|{condition.Metadata?.Group}");
                break;
        }
    }

    private static ApplicationDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        return new ApplicationDbContext(options, new TestDataScopeContext());
    }


    private sealed class FakeStrategyTemplateCatalogService : IStrategyTemplateCatalogService
    {
        public IReadOnlyCollection<StrategyTemplateSnapshot> ListResult { get; } =
        [
            new StrategyTemplateSnapshot(
                "rsi-reversal",
                "RSI Reversal",
                "Ready-to-clone published template.",
                "Reversal",
                2,
                "{ }",
                new StrategyDefinitionValidationSnapshot(true, "Valid", "Ready", [], 3),
                IsBuiltIn: true,
                IsActive: true,
                TemplateSource: "BuiltIn",
                ActiveRevisionNumber: 1,
                LatestRevisionNumber: 1,
                PublishedRevisionNumber: 1)
        ];

        public Task<IReadOnlyCollection<StrategyTemplateSnapshot>> ListAsync(CancellationToken cancellationToken = default) => Task.FromResult(ListResult);
        public Task<IReadOnlyCollection<StrategyTemplateSnapshot>> ListAllAsync(CancellationToken cancellationToken = default) => Task.FromResult(ListResult);
        public Task<StrategyTemplateSnapshot> GetAsync(string templateKey, CancellationToken cancellationToken = default)
        {
            var template = ListResult.SingleOrDefault(item => string.Equals(item.TemplateKey, templateKey, StringComparison.Ordinal));
            return template is null
                ? throw new StrategyTemplateCatalogException("TemplateNotFound", $"Strategy template '{templateKey}' was not found.")
                : Task.FromResult(template);
        }
        public Task<StrategyTemplateSnapshot> GetIncludingArchivedAsync(string templateKey, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<StrategyTemplateSnapshot> CreateCustomAsync(string ownerUserId, string templateKey, string templateName, string description, string category, string definitionJson, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<StrategyTemplateSnapshot> CloneAsync(string ownerUserId, string sourceTemplateKey, string templateKey, string templateName, string description, string category, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<StrategyTemplateSnapshot> UpdateCurrentAsync(string templateKey, string templateName, string description, string category, string definitionJson, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        

        public Task<StrategyTemplateSnapshot> ReviseAsync(
            string templateKey,
            string templateName,
            string description,
            string category,
            string definitionJson,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<StrategyTemplateSnapshot> PublishAsync(string templateKey, int revisionNumber, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<StrategyTemplateRevisionSnapshot>> ListRevisionsAsync(string templateKey, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<StrategyTemplateSnapshot> ArchiveAsync(string templateKey, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FakeStrategyVersionService : IStrategyVersionService
    {
        public StrategyVersionSnapshot? NextDraft { get; set; }
        public Exception? NextException { get; set; }
        public Guid LastDraftId { get; private set; }
        public List<CreateDraftRequest> CreateDraftRequests { get; } = [];
        public List<CreateDraftFromTemplateRequest> CreateDraftFromTemplateRequests { get; } = [];
        public List<Guid> PublishRequests { get; } = [];

        public Task<StrategyVersionSnapshot> CreateDraftAsync(Guid strategyId, string definitionJson, CancellationToken cancellationToken = default)
        {
            CreateDraftRequests.Add(new CreateDraftRequest(strategyId, definitionJson));
            if (NextException is not null)
            {
                throw NextException;
            }

            var draft = NextDraft ?? new StrategyVersionSnapshot(
                Guid.NewGuid(),
                strategyId,
                2,
                1,
                StrategyVersionStatus.Draft,
                null,
                null,
                TemplateKey: "custom",
                TemplateName: "custom",
                ValidationStatusCode: "Valid",
                ValidationSummary: "Ready",
                TemplateRevisionNumber: 1,
                TemplateSource: "Custom");

            LastDraftId = draft.StrategyVersionId;
            return Task.FromResult(draft);
        }

        public Task<StrategyVersionSnapshot> CreateDraftFromTemplateAsync(Guid strategyId, string templateKey, CancellationToken cancellationToken = default)
        {
            CreateDraftFromTemplateRequests.Add(new CreateDraftFromTemplateRequest(strategyId, templateKey));
            if (NextException is not null)
            {
                throw NextException;
            }

            var draft = NextDraft ?? new StrategyVersionSnapshot(
                Guid.NewGuid(),
                strategyId,
                2,
                1,
                StrategyVersionStatus.Draft,
                null,
                null,
                TemplateKey: templateKey,
                TemplateName: templateKey,
                ValidationStatusCode: "Valid",
                ValidationSummary: "Ready",
                TemplateRevisionNumber: 1,
                TemplateSource: "BuiltIn");

            LastDraftId = draft.StrategyVersionId;
            return Task.FromResult(draft);
        }

        public Task<StrategyVersionSnapshot> CreateDraftFromVersionAsync(Guid strategyVersionId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<StrategyVersionSnapshot> PublishAsync(Guid strategyVersionId, CancellationToken cancellationToken = default)
        {
            PublishRequests.Add(strategyVersionId);
            return Task.FromResult(new StrategyVersionSnapshot(
                strategyVersionId,
                Guid.NewGuid(),
                2,
                1,
                StrategyVersionStatus.Published,
                DateTime.UtcNow,
                null,
                ValidationStatusCode: "Valid",
                ValidationSummary: "Ready"));
        }
        public Task<StrategyVersionSnapshot> ActivateAsync(Guid strategyVersionId, string? expectedActivationToken = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<StrategyVersionSnapshot?> DeactivateAsync(Guid strategyId, string? expectedActivationToken = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<StrategyVersionSnapshot> ArchiveAsync(Guid strategyVersionId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed record CreateDraftRequest(Guid StrategyId, string DefinitionJson);
    private sealed record CreateDraftFromTemplateRequest(Guid StrategyId, string TemplateKey);

    private sealed class TestDataScopeContext : IDataScopeContext
    {
        public string? UserId => null;
        public bool HasIsolationBypass => true;
    }

    private sealed class TestTempDataProvider : ITempDataProvider
    {
        private readonly Dictionary<string, object> values = new(StringComparer.Ordinal);

        public IDictionary<string, object> LoadTempData(HttpContext context)
        {
            return new Dictionary<string, object>(values, StringComparer.Ordinal);
        }

        public void SaveTempData(HttpContext context, IDictionary<string, object> values)
        {
            this.values.Clear();
            foreach (var pair in values)
            {
                this.values[pair.Key] = pair.Value;
            }
        }
    }
}


