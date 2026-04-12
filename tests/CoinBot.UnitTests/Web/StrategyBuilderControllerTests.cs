using System.Security.Claims;
using CoinBot.Application.Abstractions.DataScope;
using CoinBot.Application.Abstractions.Strategies;
using CoinBot.Contracts.Common;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Persistence;
using CoinBot.Web.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;

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

        Assert.Same(templates, templateCatalog.ListResult);
        Assert.Equal(2, targets.Count);
        Assert.Contains(targets, item => item.Text == "Alpha Core (alpha-core)");
        Assert.Contains(targets, item => item.Text == "beta-core");
        Assert.DoesNotContain(targets, item => item.Text.Contains("gamma-core", StringComparison.Ordinal));
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
        Assert.Equal("Template ve strateji seçimi gerekli.", controller.TempData["StrategyBuilderTemplateError"]?.ToString());
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
        FakeStrategyTemplateCatalogService templateCatalogService,
        FakeStrategyVersionService strategyVersionService,
        string userId)
    {
        var controller = new StrategyBuilderController(templateCatalogService, strategyVersionService, dbContext);
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
        public Task<StrategyTemplateSnapshot> ReviseAsync(string templateKey, string templateName, string description, string category, string definitionJson, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<StrategyTemplateSnapshot> PublishAsync(string templateKey, int revisionNumber, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<StrategyTemplateRevisionSnapshot>> ListRevisionsAsync(string templateKey, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<StrategyTemplateSnapshot> ArchiveAsync(string templateKey, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FakeStrategyVersionService : IStrategyVersionService
    {
        public StrategyVersionSnapshot? NextDraft { get; set; }
        public Exception? NextException { get; set; }
        public Guid LastDraftId { get; private set; }
        public List<CreateDraftFromTemplateRequest> CreateDraftFromTemplateRequests { get; } = [];
        public List<Guid> PublishRequests { get; } = [];

        public Task<StrategyVersionSnapshot> CreateDraftAsync(Guid strategyId, string definitionJson, CancellationToken cancellationToken = default) => throw new NotSupportedException();

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


