using System.Security.Claims;
using CoinBot.Application.Abstractions.DataScope;
using CoinBot.Contracts.Common;
using CoinBot.Domain.Entities;
using CoinBot.Infrastructure.Persistence;
using CoinBot.Web.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;

namespace CoinBot.UnitTests.Web;

public sealed class RiskCenterControllerTests
{
    [Fact]
    public async Task Index_ReturnsDefaultModel_WhenRiskProfileIsMissing()
    {
        await using var dbContext = CreateDbContext("risk-user-01");
        var controller = CreateController(dbContext, "risk-user-01");

        var result = await controller.Index(CancellationToken.None);

        var viewResult = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<RiskCenterViewModel>(viewResult.Model);

        Assert.False(model.HasProfile);
        Assert.Equal("Eksik", model.StatusText);
        Assert.Equal(1m, model.Input.MaxLeverage);
        Assert.Equal(3, model.Input.MaxConcurrentPositions);
    }

    [Fact]
    public async Task Index_Post_CreatesRiskProfile_ForCurrentUser()
    {
        await using var dbContext = CreateDbContext("risk-user-02");
        var controller = CreateController(dbContext, "risk-user-02");
        var model = new RiskCenterViewModel { Input = CreateInput(profileName: "Pilot Risk") };

        var result = await controller.Index(model, CancellationToken.None);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(RiskCenterController.Index), redirect.ActionName);

        var profile = await dbContext.RiskProfiles.SingleAsync();
        Assert.Equal("risk-user-02", profile.OwnerUserId);
        Assert.Equal("Pilot Risk", profile.ProfileName);
        Assert.Equal(2m, profile.MaxDailyLossPercentage);
        Assert.Equal(6m, profile.MaxWeeklyLossPercentage);
        Assert.Equal(10m, profile.MaxPositionSizePercentage);
        Assert.Equal(25m, profile.MaxSymbolExposurePercentage);
        Assert.Equal(1m, profile.MaxLeverage);
        Assert.Equal(3, profile.MaxConcurrentPositions);
        Assert.False(profile.KillSwitchEnabled);
        Assert.Equal("Risk tercihi kaydedildi.", controller.TempData["RiskCenterSuccess"]?.ToString());
    }

    [Fact]
    public async Task Index_Post_UpdatesExistingRiskProfile_ForCurrentUser()
    {
        await using var dbContext = CreateDbContext("risk-user-03");
        var profileId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        dbContext.RiskProfiles.Add(new RiskProfile
        {
            Id = profileId,
            OwnerUserId = "risk-user-03",
            ProfileName = "Eski Profil",
            MaxDailyLossPercentage = 1m,
            MaxWeeklyLossPercentage = 5m,
            MaxPositionSizePercentage = 8m,
            MaxSymbolExposurePercentage = 15m,
            MaxLeverage = 1m,
            MaxConcurrentPositions = 2,
            KillSwitchEnabled = false
        });
        await dbContext.SaveChangesAsync();

        var controller = CreateController(dbContext, "risk-user-03");
        var model = new RiskCenterViewModel
        {
            Input = CreateInput(profileName: "Yeni Profil", maxLeverage: 2m, maxConcurrentPositions: 4, killSwitchEnabled: true)
        };

        var result = await controller.Index(model, CancellationToken.None);

        Assert.IsType<RedirectToActionResult>(result);
        var profile = await dbContext.RiskProfiles.SingleAsync();
        Assert.Equal(profileId, profile.Id);
        Assert.Equal("Yeni Profil", profile.ProfileName);
        Assert.Equal(2m, profile.MaxLeverage);
        Assert.Equal(4, profile.MaxConcurrentPositions);
        Assert.True(profile.KillSwitchEnabled);
    }

    [Fact]
    public async Task Index_Post_RejectsInvalidRiskLimits_FailClosed()
    {
        await using var dbContext = CreateDbContext("risk-user-04");
        var controller = CreateController(dbContext, "risk-user-04");
        var model = new RiskCenterViewModel
        {
            Input = CreateInput(
                maxDailyLossPercentage: 0m,
                maxWeeklyLossPercentage: 0.5m,
                maxPositionSizePercentage: 101m,
                maxLeverage: 50m,
                maxConcurrentPositions: 0)
        };

        var result = await controller.Index(model, CancellationToken.None);

        var viewResult = Assert.IsType<ViewResult>(result);
        var viewModel = Assert.IsType<RiskCenterViewModel>(viewResult.Model);
        Assert.False(viewModel.HasProfile);
        Assert.False(controller.ModelState.IsValid);
        Assert.Empty(await dbContext.RiskProfiles.ToListAsync());
    }

    [Fact]
    public void Controller_RequiresRiskManagement_AndPostRequiresAntiForgery()
    {
        var authorizeAttribute = Assert.Single(
            typeof(RiskCenterController)
                .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
                .Cast<AuthorizeAttribute>());
        var antiForgeryAttribute = Assert.Single(
            typeof(RiskCenterController)
                .GetMethod(nameof(RiskCenterController.Index), [typeof(RiskCenterViewModel), typeof(CancellationToken)])!
                .GetCustomAttributes(typeof(ValidateAntiForgeryTokenAttribute), inherit: true)
                .Cast<ValidateAntiForgeryTokenAttribute>());

        Assert.Equal(ApplicationPolicies.RiskManagement, authorizeAttribute.Policy);
        Assert.NotNull(antiForgeryAttribute);
    }

    private static RiskCenterInputModel CreateInput(
        string profileName = "Varsayılan Risk Profili",
        decimal maxDailyLossPercentage = 2m,
        decimal? maxWeeklyLossPercentage = 6m,
        decimal maxPositionSizePercentage = 10m,
        decimal? maxSymbolExposurePercentage = 25m,
        decimal maxLeverage = 1m,
        int? maxConcurrentPositions = 3,
        bool killSwitchEnabled = false)
    {
        return new RiskCenterInputModel
        {
            ProfileName = profileName,
            MaxDailyLossPercentage = maxDailyLossPercentage,
            MaxWeeklyLossPercentage = maxWeeklyLossPercentage,
            MaxPositionSizePercentage = maxPositionSizePercentage,
            MaxSymbolExposurePercentage = maxSymbolExposurePercentage,
            MaxLeverage = maxLeverage,
            MaxConcurrentPositions = maxConcurrentPositions,
            KillSwitchEnabled = killSwitchEnabled
        };
    }

    private static RiskCenterController CreateController(ApplicationDbContext dbContext, string userId)
    {
        var controller = new RiskCenterController(dbContext);
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

    private static ApplicationDbContext CreateDbContext(string userId)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        return new ApplicationDbContext(options, new TestDataScopeContext(userId));
    }

    private sealed class TestDataScopeContext(string userId) : IDataScopeContext
    {
        public string? UserId => userId;
        public bool HasIsolationBypass => false;
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
