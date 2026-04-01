using Microsoft.AspNetCore.Authorization;
using System.Security.Claims;
using CoinBot.Application.Abstractions.ExchangeCredentials;
using CoinBot.Contracts.Common;
using CoinBot.Infrastructure.Credentials;
using CoinBot.Web.ViewModels.Exchanges;
using Microsoft.AspNetCore.Mvc;

namespace CoinBot.Web.Controllers;

[Authorize]
public sealed class OnboardingController(IUserExchangeCommandCenterService userExchangeCommandCenterService) : Controller
{
    private const string CredentialSecurityConfigurationErrorMessage =
        "Credential guvenlik yapilandirmasi eksik oldugu icin API key kaydedilemedi. Local/dev ortaminda encryption key tanimlanmadan dogrulama baslatilamaz.";

    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();

        if (userId is null)
        {
            return Challenge();
        }

        ViewData["OnboardingExchangeSnapshot"] = await userExchangeCommandCenterService.GetSnapshotAsync(userId, cancellationToken);
        return View();
    }

    public IActionResult ExchangeSecurity()
    {
        return View();
    }

    [Authorize(Policy = ApplicationPolicies.ExchangeManagement)]
    public async Task<IActionResult> ExchangeConnect(CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();

        if (userId is null)
        {
            return Challenge();
        }

        var snapshot = await userExchangeCommandCenterService.GetSnapshotAsync(userId, cancellationToken);
        var viewModel = new UserExchangeCommandCenterPageViewModel
        {
            Snapshot = snapshot,
            Form = new BinanceCredentialConnectInputModel
            {
                ExchangeAccountId = snapshot.Accounts.FirstOrDefault()?.ExchangeAccountId,
                RequestedEnvironment = snapshot.Environment.EffectiveEnvironment,
                RequestedTradeMode = ExchangeTradeModeSelection.Spot
            },
            SuccessMessage = TempData["ExchangeConnectSuccess"] as string,
            ErrorMessage = TempData["ExchangeConnectError"] as string,
            SubmitAction = nameof(ConnectBinance),
            SubmitController = "Onboarding",
            ShowOnboardingFooterActions = true
        };

        return View(viewModel);
    }

    [HttpPost]
    [Authorize(Policy = ApplicationPolicies.ExchangeManagement)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ConnectBinance(
        [Bind(Prefix = "Form")] BinanceCredentialConnectInputModel input,
        CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();

        if (userId is null)
        {
            return Challenge();
        }

        if (!ModelState.IsValid)
        {
            TempData["ExchangeConnectError"] = "API key, secret, ortam ve işlem kapsamı gerekli.";
            return RedirectToAction(nameof(ExchangeConnect));
        }

        ConnectUserBinanceCredentialResult result;

        try
        {
            result = await userExchangeCommandCenterService.ConnectBinanceAsync(
                new ConnectUserBinanceCredentialRequest(
                    userId,
                    input.ExchangeAccountId,
                    input.ApiKey,
                    input.ApiSecret,
                    input.RequestedEnvironment,
                    input.RequestedTradeMode,
                    Actor: $"user:{userId}",
                    CorrelationId: HttpContext.TraceIdentifier),
                cancellationToken);
        }
        catch (CredentialSecurityConfigurationException)
        {
            TempData["ExchangeConnectError"] = CredentialSecurityConfigurationErrorMessage;
            return RedirectToAction(nameof(ExchangeConnect));
        }

        TempData[result.IsValid ? "ExchangeConnectSuccess" : "ExchangeConnectError"] = result.IsValid
            ? result.UserMessage
            : result.SafeFailureReason ?? result.UserMessage;

        return RedirectToAction(nameof(ExchangeConnect));
    }

    private string? GetCurrentUserId()
    {
        return User.FindFirstValue(ClaimTypes.NameIdentifier);
    }
}
