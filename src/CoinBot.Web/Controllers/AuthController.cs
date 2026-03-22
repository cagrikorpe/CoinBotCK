using CoinBot.Contracts.Common;
using CoinBot.Infrastructure.Identity;
using CoinBot.Web.ViewModels.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace CoinBot.Web.Controllers;

[AllowAnonymous]
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
    public IActionResult Login(string? returnUrl = null)
    {
        if (signInManager.IsSignedIn(User))
        {
            return RedirectToAction("Index", "Home");
        }

        return View(new LoginViewModel { ReturnUrl = returnUrl });
    }

    [HttpPost]
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

        return RedirectToLocal(model.ReturnUrl);
    }

    [HttpGet]
    public IActionResult Register()
    {
        if (signInManager.IsSignedIn(User))
        {
            return RedirectToAction("Index", "Home");
        }

        return View(new RegisterViewModel());
    }

    [HttpPost]
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

        TempData["AuthSuccess"] = "Kayıt tamamlandı. Giriş yapabilirsiniz.";
        return RedirectToAction(nameof(Login));
    }

    [HttpGet]
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

    private static string NormalizeOtpCode(string code)
    {
        return new string(code.Where(char.IsDigit).ToArray());
    }
}
