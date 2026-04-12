using System.Security.Claims;
using CoinBot.Contracts.Common;
using CoinBot.Domain.Entities;
using CoinBot.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CoinBot.Web.Controllers;

[RedirectSuperAdminToAdminOverview]
[Authorize(Policy = ApplicationPolicies.RiskManagement)]
public sealed class RiskCenterController(ApplicationDbContext dbContext) : Controller
{
    internal const string DefaultProfileName = "Varsayılan Risk Profili";

    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId is null)
        {
            return Challenge();
        }

        var profile = await GetLatestProfileAsync(userId, asTracking: false, cancellationToken);
        return View(CreateViewModel(profile));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Index(RiskCenterViewModel model, CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId is null)
        {
            return Challenge();
        }

        var input = model.Input ?? RiskCenterInputModel.CreateDefault();
        Normalize(input);
        Validate(input);

        var existingProfile = await GetLatestProfileAsync(userId, asTracking: true, cancellationToken);
        if (!ModelState.IsValid)
        {
            return View(CreateViewModel(existingProfile, input));
        }

        var profile = existingProfile ?? new RiskProfile
        {
            Id = Guid.NewGuid(),
            OwnerUserId = userId
        };

        Apply(profile, input);
        if (existingProfile is null)
        {
            dbContext.RiskProfiles.Add(profile);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        TempData["RiskCenterSuccess"] = "Risk tercihi kaydedildi.";

        return RedirectToAction(nameof(Index));
    }

    private async Task<RiskProfile?> GetLatestProfileAsync(
        string userId,
        bool asTracking,
        CancellationToken cancellationToken)
    {
        var query = dbContext.RiskProfiles
            .Where(entity => entity.OwnerUserId == userId && !entity.IsDeleted)
            .OrderByDescending(entity => entity.UpdatedDate)
            .ThenByDescending(entity => entity.CreatedDate)
            .ThenByDescending(entity => entity.Id);

        return asTracking
            ? await query.FirstOrDefaultAsync(cancellationToken)
            : await query.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
    }

    private static RiskCenterViewModel CreateViewModel(RiskProfile? profile, RiskCenterInputModel? input = null)
    {
        var form = input ?? (profile is null
            ? RiskCenterInputModel.CreateDefault()
            : RiskCenterInputModel.FromProfile(profile));

        var statusText = profile is null
            ? "Eksik"
            : profile.KillSwitchEnabled
                ? "Acil durdurma aktif"
                : "Hazır";

        var statusSummary = profile is null
            ? "Risk tercihi eksik. Botlar işlem başlatmaz."
            : profile.KillSwitchEnabled
                ? "Risk tercihi var, ancak acil durdurma açık."
                : "Risk tercihi kaydedildi.";

        return new RiskCenterViewModel
        {
            Input = form,
            HasProfile = profile is not null,
            LastUpdatedAtUtc = profile?.UpdatedDate,
            StatusText = statusText,
            StatusSummary = statusSummary
        };
    }

    private void Validate(RiskCenterInputModel input)
    {
        if (string.IsNullOrWhiteSpace(input.ProfileName))
        {
            ModelState.AddModelError("Input.ProfileName", "Profil adı gerekli.");
        }
        else if (input.ProfileName.Length > 64)
        {
            ModelState.AddModelError("Input.ProfileName", "Profil adı 64 karakteri geçemez.");
        }

        ValidateRange("Input.MaxDailyLossPercentage", input.MaxDailyLossPercentage, 0.1m, 100m, "Günlük zarar limiti 0 ile 100 arasında olmalı.");
        if (input.MaxWeeklyLossPercentage is decimal weeklyLoss)
        {
            ValidateRange("Input.MaxWeeklyLossPercentage", weeklyLoss, input.MaxDailyLossPercentage, 100m, "Haftalık zarar limiti günlük limitten küçük olamaz.");
        }

        ValidateRange("Input.MaxPositionSizePercentage", input.MaxPositionSizePercentage, 0.1m, 100m, "Pozisyon limiti 0 ile 100 arasında olmalı.");
        if (input.MaxSymbolExposurePercentage is decimal symbolExposure)
        {
            ValidateRange("Input.MaxSymbolExposurePercentage", symbolExposure, 0.1m, 100m, "Sembol limiti 0 ile 100 arasında olmalı.");
        }

        ValidateRange("Input.MaxLeverage", input.MaxLeverage, 1m, 25m, "Kaldıraç 1 ile 25 arasında olmalı.");
        if (input.MaxConcurrentPositions is null)
        {
            ModelState.AddModelError("Input.MaxConcurrentPositions", "Açık işlem limiti gerekli.");
        }
        else if (input.MaxConcurrentPositions is < 1 or > 50)
        {
            ModelState.AddModelError("Input.MaxConcurrentPositions", "Açık işlem limiti 1 ile 50 arasında olmalı.");
        }
    }

    private void ValidateRange(string key, decimal value, decimal min, decimal max, string message)
    {
        if (value < min || value > max)
        {
            ModelState.AddModelError(key, message);
        }
    }

    private static void Apply(RiskProfile profile, RiskCenterInputModel input)
    {
        profile.ProfileName = input.ProfileName;
        profile.MaxDailyLossPercentage = input.MaxDailyLossPercentage;
        profile.MaxWeeklyLossPercentage = input.MaxWeeklyLossPercentage;
        profile.MaxPositionSizePercentage = input.MaxPositionSizePercentage;
        profile.MaxSymbolExposurePercentage = input.MaxSymbolExposurePercentage;
        profile.MaxLeverage = input.MaxLeverage;
        profile.MaxConcurrentPositions = input.MaxConcurrentPositions;
        profile.KillSwitchEnabled = input.KillSwitchEnabled;
    }

    private static void Normalize(RiskCenterInputModel input)
    {
        input.ProfileName = string.IsNullOrWhiteSpace(input.ProfileName)
            ? DefaultProfileName
            : input.ProfileName.Trim();
    }

    private string? GetCurrentUserId()
    {
        return User.FindFirstValue(ClaimTypes.NameIdentifier);
    }
}

public sealed class RiskCenterViewModel
{
    public RiskCenterInputModel Input { get; set; } = RiskCenterInputModel.CreateDefault();
    public bool HasProfile { get; set; }
    public DateTime? LastUpdatedAtUtc { get; set; }
    public string StatusText { get; set; } = "Eksik";
    public string StatusSummary { get; set; } = "Risk tercihi eksik. Botlar işlem başlatmaz.";
}

public sealed class RiskCenterInputModel
{
    public string ProfileName { get; set; } = string.Empty;
    public decimal MaxDailyLossPercentage { get; set; }
    public decimal? MaxWeeklyLossPercentage { get; set; }
    public decimal MaxPositionSizePercentage { get; set; }
    public decimal? MaxSymbolExposurePercentage { get; set; }
    public decimal MaxLeverage { get; set; }
    public int? MaxConcurrentPositions { get; set; }
    public bool KillSwitchEnabled { get; set; }

    public static RiskCenterInputModel CreateDefault()
    {
        return new RiskCenterInputModel
        {
            ProfileName = RiskCenterController.DefaultProfileName,
            MaxDailyLossPercentage = 2m,
            MaxWeeklyLossPercentage = 6m,
            MaxPositionSizePercentage = 10m,
            MaxSymbolExposurePercentage = 25m,
            MaxLeverage = 1m,
            MaxConcurrentPositions = 3,
            KillSwitchEnabled = false
        };
    }

    public static RiskCenterInputModel FromProfile(RiskProfile profile)
    {
        return new RiskCenterInputModel
        {
            ProfileName = profile.ProfileName,
            MaxDailyLossPercentage = profile.MaxDailyLossPercentage,
            MaxWeeklyLossPercentage = profile.MaxWeeklyLossPercentage,
            MaxPositionSizePercentage = profile.MaxPositionSizePercentage,
            MaxSymbolExposurePercentage = profile.MaxSymbolExposurePercentage,
            MaxLeverage = profile.MaxLeverage,
            MaxConcurrentPositions = profile.MaxConcurrentPositions,
            KillSwitchEnabled = profile.KillSwitchEnabled
        };
    }
}
