using CoinBot.Contracts.Common;
using CoinBot.Infrastructure.Identity;
using CoinBot.Web.ViewModels.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace CoinBot.Web.Controllers;

public class AuthController : Controller
{
    private readonly UserManager<ApplicationUser> userManager;
    private readonly SignInManager<ApplicationUser> signInManager;

    public AuthController(UserManager<ApplicationUser> userManager, SignInManager<ApplicationUser> signInManager)
    {
        this.userManager = userManager;
        this.signInManager = signInManager;
    }

    [HttpGet]
    [AllowAnonymous]
    public IActionResult Login(string? returnUrl = null)
    {
        if (signInManager.IsSignedIn(User))
        {
            return RedirectToLocal(returnUrl);
        }

        return View(new LoginViewModel { ReturnUrl = returnUrl });
    }

    [HttpPost]
    [AllowAnonymous]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(LoginViewModel model)
    {
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var user = await FindUserAsync(model.EmailOrUserName);

        if (user is null || string.IsNullOrWhiteSpace(user.UserName))
        {
            ModelState.AddModelError(string.Empty, "Giriş bilgileri doğrulanamadı.");
            return View(model);
        }

        var result = await signInManager.PasswordSignInAsync(user.UserName, model.Password, model.RememberMe, lockoutOnFailure: true);
        var shouldSynchronizeClaims = result.Succeeded || result.RequiresTwoFactor;

        if (shouldSynchronizeClaims)
        {
            var claimSyncResult = await SynchronizePermissionClaimsAsync(user);

            if (!claimSyncResult.Succeeded)
            {
                if (result.Succeeded)
                {
                    await signInManager.SignOutAsync();
                }

                AddIdentityErrors(claimSyncResult);
                ModelState.AddModelError(string.Empty, "Oturum başlatılamadı. Yetkilendirme bilgileri hazırlanamadı.");
                return View(model);
            }
        }

        if (result.RequiresTwoFactor)
        {
            return RedirectToAction(nameof(Mfa), new { model.ReturnUrl, model.RememberMe });
        }

        if (result.IsLockedOut)
        {
            ModelState.AddModelError(string.Empty, "Hesap geçici olarak kilitlendi. Lütfen daha sonra tekrar deneyin.");
            return View(model);
        }

        if (result.IsNotAllowed)
        {
            ModelState.AddModelError(string.Empty, "Bu hesap için giriş izni henüz tamamlanmadı.");
            return View(model);
        }

        if (!result.Succeeded)
        {
            ModelState.AddModelError(string.Empty, "Giriş bilgileri doğrulanamadı.");
            return View(model);
        }

        await signInManager.RefreshSignInAsync(user);
        return RedirectToLocal(model.ReturnUrl);
    }

    [HttpGet]
    [AllowAnonymous]
    public IActionResult Register(string? returnUrl = null)
    {
        if (signInManager.IsSignedIn(User))
        {
            return RedirectToLocal(returnUrl);
        }

        return View(new RegisterViewModel { ReturnUrl = returnUrl });
    }

    [HttpPost]
    [AllowAnonymous]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Register(RegisterViewModel model)
    {
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        if (await userManager.FindByEmailAsync(model.Email) is not null)
        {
            ModelState.AddModelError(nameof(model.Email), "Bu e-posta adresi zaten kayıtlı.");
            return View(model);
        }

        var user = new ApplicationUser
        {
            UserName = model.Email,
            Email = model.Email,
            FullName = model.FullName,
            LockoutEnabled = true,
            TwoFactorEnabled = false
        };

        var createResult = await userManager.CreateAsync(user, model.Password);

        if (!createResult.Succeeded)
        {
            foreach (var error in createResult.Errors)
            {
                ModelState.AddModelError(string.Empty, error.Description);
            }

            return View(model);
        }

        var addRoleResult = await userManager.AddToRoleAsync(user, ApplicationRoles.User);

        if (!addRoleResult.Succeeded)
        {
            await userManager.DeleteAsync(user);

            foreach (var error in addRoleResult.Errors)
            {
                ModelState.AddModelError(string.Empty, error.Description);
            }

            return View(model);
        }

        var claimSyncResult = await SynchronizePermissionClaimsAsync(user);

        if (!claimSyncResult.Succeeded)
        {
            await userManager.DeleteAsync(user);
            AddIdentityErrors(claimSyncResult);
            return View(model);
        }

        TempData["AuthSuccess"] = "Kayıt tamamlandı. Giriş yapabilirsiniz.";
        return RedirectToAction(nameof(Login), new { returnUrl = model.ReturnUrl });
    }

    [HttpGet]
    [AllowAnonymous]
    public IActionResult AccessDenied()
    {
        if (!(User.Identity?.IsAuthenticated ?? false))
        {
            return RedirectToAction(nameof(Login));
        }

        ViewData["Title"] = "Access Denied";
        ViewData["AuthTitle"] = "Yetki gerekli";
        ViewData["AuthDescription"] = "Bu yüzey için hesabınızda gerekli rol veya permission claim bulunmuyor.";
        ViewData["AuthEyebrow"] = "Authorization";
        ViewData["AuthStage"] = "Access Denied";
        ViewData["AuthProgress"] = "Policy";
        ViewData["AuthHighlights"] = new[]
        {
            "Kritik admin ve trade alanları fail-closed çalışır",
            "Rol ve permission claim eksikse erişim açılmaz",
            "Gerekirse yöneticinizden ek erişim isteyin"
        };

        return View();
    }

    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> Mfa(string? returnUrl = null, bool rememberMe = false)
    {
        if (await signInManager.GetTwoFactorAuthenticationUserAsync() is null)
        {
            TempData["AuthError"] = "Çok faktörlü doğrulama oturumu bulunamadı.";
            return RedirectToAction(nameof(Login));
        }

        return View(new MfaViewModel
        {
            ReturnUrl = returnUrl,
            RememberMe = rememberMe
        });
    }

    [HttpPost]
    [AllowAnonymous]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Mfa(MfaViewModel model)
    {
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var user = await signInManager.GetTwoFactorAuthenticationUserAsync();

        if (user is null)
        {
            TempData["AuthError"] = "Çok faktörlü doğrulama oturumu bulunamadı.";
            return RedirectToAction(nameof(Login));
        }

        var claimSyncResult = await SynchronizePermissionClaimsAsync(user);

        if (!claimSyncResult.Succeeded)
        {
            AddIdentityErrors(claimSyncResult);
            ModelState.AddModelError(string.Empty, "Oturum doğrulanamadı. Yetkilendirme bilgileri hazırlanamadı.");
            return View(model);
        }

        var code = NormalizeOtpCode(model.Code);
        var result = await signInManager.TwoFactorAuthenticatorSignInAsync(code, model.RememberMe, rememberClient: false);

        if (result.IsLockedOut)
        {
            ModelState.AddModelError(string.Empty, "Hesap geçici olarak kilitlendi. Lütfen daha sonra tekrar deneyin.");
            return View(model);
        }

        if (!result.Succeeded)
        {
            ModelState.AddModelError(string.Empty, "Doğrulama kodu geçersiz.");
            return View(model);
        }

        return RedirectToLocal(model.ReturnUrl);
    }

    [HttpPost]
    [Authorize]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        await signInManager.SignOutAsync();
        TempData["AuthSuccess"] = "Oturum güvenli şekilde kapatıldı.";

        return RedirectToAction(nameof(Login));
    }

    private async Task<ApplicationUser?> FindUserAsync(string emailOrUserName)
    {
        var normalizedValue = emailOrUserName.Trim();

        if (string.IsNullOrWhiteSpace(normalizedValue))
        {
            return null;
        }

        return normalizedValue.Contains('@')
            ? await userManager.FindByEmailAsync(normalizedValue)
            : await userManager.FindByNameAsync(normalizedValue);
    }

    private IActionResult RedirectToLocal(string? returnUrl)
    {
        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
        {
            return LocalRedirect(returnUrl);
        }

        return RedirectToAction("Index", "Home");
    }

    private async Task<IdentityResult> SynchronizePermissionClaimsAsync(ApplicationUser user)
    {
        var roles = await userManager.GetRolesAsync(user);
        var expectedClaims = roles
            .SelectMany(ApplicationRoleClaims.GetClaims)
            .GroupBy(claim => new { claim.Type, claim.Value })
            .Select(group => group.First())
            .ToArray();
        var existingClaims = (await userManager.GetClaimsAsync(user))
            .Where(claim => claim.Type == ApplicationClaimTypes.Permission)
            .ToArray();
        var claimsToAdd = expectedClaims
            .Where(expectedClaim => !existingClaims.Any(existingClaim =>
                existingClaim.Type == expectedClaim.Type &&
                existingClaim.Value == expectedClaim.Value))
            .ToArray();
        var claimsToRemove = existingClaims
            .Where(existingClaim => !expectedClaims.Any(expectedClaim =>
                expectedClaim.Type == existingClaim.Type &&
                expectedClaim.Value == existingClaim.Value))
            .ToArray();

        if (claimsToRemove.Length > 0)
        {
            var removeClaimsResult = await userManager.RemoveClaimsAsync(user, claimsToRemove);

            if (!removeClaimsResult.Succeeded)
            {
                return removeClaimsResult;
            }
        }

        if (claimsToAdd.Length == 0)
        {
            return IdentityResult.Success;
        }

        return await userManager.AddClaimsAsync(user, claimsToAdd);
    }

    private void AddIdentityErrors(IdentityResult identityResult)
    {
        foreach (var error in identityResult.Errors)
        {
            ModelState.AddModelError(string.Empty, error.Description);
        }
    }

    private static string NormalizeOtpCode(string code)
    {
        return new string(code.Where(char.IsDigit).ToArray());
    }
}
