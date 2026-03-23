using System.Security.Claims;
using System.Text.Json;
using CoinBot.Application.Abstractions.Mfa;
using CoinBot.Web.ViewModels.Settings;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;

namespace CoinBot.Web.Controllers;

[Authorize]
public sealed class SettingsController : Controller
{
    private const string RecoveryCodesTempDataKey = "MfaRecoveryCodes";
    private readonly IMfaManagementService mfaManagementService;

    public SettingsController(IMfaManagementService mfaManagementService)
    {
        this.mfaManagementService = mfaManagementService;
    }

    public IActionResult Index() => View();

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
}
