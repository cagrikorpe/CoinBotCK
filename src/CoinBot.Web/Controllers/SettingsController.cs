using System.Security.Claims;
using System.Text.Json;
using CoinBot.Application.Abstractions.Mfa;
using CoinBot.Application.Abstractions.Settings;
using CoinBot.Web.ViewModels.Settings;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CoinBot.Web.Controllers;

[Authorize]
public sealed class SettingsController : Controller
{
    private const string RecoveryCodesTempDataKey = "MfaRecoveryCodes";
    private readonly IMfaManagementService mfaManagementService;
    private readonly IUserSettingsService userSettingsService;

    public SettingsController(
        IMfaManagementService mfaManagementService,
        IUserSettingsService userSettingsService)
    {
        this.mfaManagementService = mfaManagementService;
        this.userSettingsService = userSettingsService;
    }

    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();

        if (userId is null)
        {
            return Challenge();
        }

        var snapshot = await userSettingsService.GetAsync(userId, cancellationToken);

        if (snapshot is null)
        {
            return Challenge();
        }

        return View(BuildSettingsViewModel(snapshot));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Index(
        [Bind(Prefix = "Form")] TimeZoneSettingsInputModel form,
        CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();

        if (userId is null)
        {
            return Challenge();
        }

        var snapshot = await userSettingsService.GetAsync(userId, cancellationToken);

        if (snapshot is null)
        {
            return Challenge();
        }

        if (!ModelState.IsValid)
        {
            return View(BuildSettingsViewModel(snapshot, form));
        }

        var result = await userSettingsService.SaveAsync(
            userId,
            new UserSettingsSaveCommand(form.PreferredTimeZoneId),
            $"user:{userId}",
            HttpContext.TraceIdentifier,
            cancellationToken);

        if (result.IsSuccessful)
        {
            TempData["SettingsSuccess"] = "Saat dilimi ayarı kaydedildi.";
            return RedirectToAction(nameof(Index));
        }

        ModelState.AddModelError(string.Empty, result.FailureReason ?? "Saat dilimi kaydedilemedi.");
        var refreshedSnapshot = await userSettingsService.GetAsync(userId, cancellationToken);
        return View(BuildSettingsViewModel(refreshedSnapshot ?? snapshot, form));
    }

    [HttpGet]
    public async Task<IActionResult> Mfa(CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();

        if (userId is null)
        {
            return Challenge();
        }

        return View(await BuildMfaViewModelAsync(userId, cancellationToken));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> StartMfaSetup(CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();

        if (userId is null)
        {
            return Challenge();
        }

        var setup = await mfaManagementService.GetAuthenticatorSetupAsync(userId, createIfMissing: true, cancellationToken);
        TempData[setup is null ? "MfaError" : "MfaSuccess"] = setup is null
            ? "Authenticator kurulumu baslatilamadi."
            : "Authenticator kurulumu baslatildi. Uygulamadaki kodu dogrulayarak 2FA'yi etkinlestirin.";

        return RedirectToAction(nameof(Mfa));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EnableMfa(string? code, CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();

        if (userId is null)
        {
            return Challenge();
        }

        var recoveryCodes = await mfaManagementService.EnableAuthenticatorAsync(userId, code ?? string.Empty, cancellationToken);

        if (recoveryCodes is null)
        {
            TempData["MfaError"] = "Authenticator kodu dogrulanamadi.";
            return RedirectToAction(nameof(Mfa));
        }

        TempData["MfaSuccess"] = "2FA etkinlestirildi.";
        TempData[RecoveryCodesTempDataKey] = JsonSerializer.Serialize(recoveryCodes);
        return RedirectToAction(nameof(Mfa));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DisableMfa(string? code, CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();

        if (userId is null)
        {
            return Challenge();
        }

        var disabled = await mfaManagementService.DisableAsync(userId, code ?? string.Empty, cancellationToken);
        TempData[disabled ? "MfaSuccess" : "MfaError"] = disabled
            ? "2FA kapatildi."
            : "2FA kapatma istegi dogrulanamadi.";

        return RedirectToAction(nameof(Mfa));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RegenerateRecoveryCodes(string? code, CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();

        if (userId is null)
        {
            return Challenge();
        }

        var recoveryCodes = await mfaManagementService.RegenerateRecoveryCodesAsync(userId, code ?? string.Empty, cancellationToken);

        if (recoveryCodes is null)
        {
            TempData["MfaError"] = "Recovery code'lar yenilenemedi.";
            return RedirectToAction(nameof(Mfa));
        }

        TempData["MfaSuccess"] = "Recovery code'lar yenilendi.";
        TempData[RecoveryCodesTempDataKey] = JsonSerializer.Serialize(recoveryCodes);
        return RedirectToAction(nameof(Mfa));
    }

    private async Task<MfaSettingsViewModel> BuildMfaViewModelAsync(string userId, CancellationToken cancellationToken)
    {
        var status = await mfaManagementService.GetStatusAsync(userId, cancellationToken);
        var setup = status.HasPendingAuthenticatorEnrollment
            ? await mfaManagementService.GetAuthenticatorSetupAsync(userId, cancellationToken: cancellationToken)
            : null;

        return new MfaSettingsViewModel
        {
            IsMfaEnabled = status.IsMfaEnabled,
            IsTotpEnabled = status.IsTotpEnabled,
            IsEmailOtpEnabled = status.IsEmailOtpEnabled,
            PreferredProvider = status.PreferredProvider,
            HasPendingAuthenticatorEnrollment = status.HasPendingAuthenticatorEnrollment,
            ActiveRecoveryCodeCount = status.ActiveRecoveryCodeCount,
            UpdatedAtUtc = status.UpdatedAtUtc,
            SharedKey = setup?.SharedKey,
            DisplaySharedKey = setup?.DisplaySharedKey,
            AuthenticatorUri = setup?.AuthenticatorUri,
            RecoveryCodes = ExtractRecoveryCodesFromTempData()
        };
    }

    private IReadOnlyList<string> ExtractRecoveryCodesFromTempData()
    {
        if (TempData[RecoveryCodesTempDataKey] is not string serializedCodes || string.IsNullOrWhiteSpace(serializedCodes))
        {
            return [];
        }

        return JsonSerializer.Deserialize<string[]>(serializedCodes) ?? [];
    }

    private string? GetCurrentUserId()
    {
        return User.FindFirstValue(ClaimTypes.NameIdentifier);
    }

    private static SettingsIndexViewModel BuildSettingsViewModel(
        UserSettingsSnapshot snapshot,
        TimeZoneSettingsInputModel? formOverride = null)
    {
        return new SettingsIndexViewModel
        {
            Form = formOverride ?? new TimeZoneSettingsInputModel
            {
                PreferredTimeZoneId = snapshot.PreferredTimeZoneId
            },
            TimeZoneOptions = snapshot.TimeZoneOptions
                .Select(option => new TimeZoneOptionViewModel(option.TimeZoneId, option.DisplayName))
                .ToArray(),
            SuccessMessage = null,
            ErrorMessage = null
        };
    }
}
