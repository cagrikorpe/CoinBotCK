using System.Security.Claims;
using CoinBot.Application.Abstractions.Administration;
using CoinBot.Application.Abstractions.Auditing;
using CoinBot.Application.Abstractions.Dashboard;
using CoinBot.Contracts.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CoinBot.Web.Controllers;

[RedirectSuperAdminToAdminOverview]
[Authorize(Policy = ApplicationPolicies.TradeOperations)]
public sealed class PositionsController(
    IUserDashboardPortfolioReadModelService userDashboardPortfolioReadModelService,
    IAdminManualCloseService adminManualCloseService,
    IAuditLogService auditLogService) : Controller
{
    private const string ManualCloseSuccessTempDataKey = "PositionsManualCloseSuccess";
    private const string ManualCloseErrorTempDataKey = "PositionsManualCloseError";

    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        ViewData["Title"] = "Pozisyonlar";
        ViewData["ActiveNav"] = "Positions";
        ViewData["PageDescription"] = "Açık ve kapalı pozisyonları, unrealized/realized PnL özetini ve emir akışını tek operasyondan izlemek için foundation ekranı.";
        ViewData["BreadcrumbItems"] = new[] { "Execution", "Pozisyonlar" };
        ViewData["DefaultTab"] = "positions";
        return View(await GetPortfolioSnapshotAsync(cancellationToken));
    }

    public async Task<IActionResult> History(CancellationToken cancellationToken)
    {
        ViewData["Title"] = "Emir Geçmişi";
        ViewData["ActiveNav"] = "OrderHistory";
        ViewData["PageDescription"] = "Pozisyonlar ve emir geçmişi foundation ekranı içinde order history sekmesine odaklı görünüm.";
        ViewData["BreadcrumbItems"] = new[] { "Execution", "Emir Geçmişi" };
        ViewData["DefaultTab"] = "history";
        return View("Index", await GetPortfolioSnapshotAsync(cancellationToken));
    }

    [HttpPost("Positions/ManualClose")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ManualClose(
        string? botId,
        [FromForm(Name = "accountScope")] string? accountScope,
        string? symbol,
        [FromForm] bool confirmClose,
        CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId is null)
        {
            return Challenge();
        }

        var normalizedSymbol = NormalizeSymbol(symbol);
        if (!confirmClose)
        {
            await WriteManualCloseAuditAsync(
                userId,
                "User.Positions.ManualCloseBlocked",
                normalizedSymbol,
                "Manual close blocked because confirmation was not acknowledged.",
                "Blocked",
                cancellationToken);
            TempData[ManualCloseErrorTempDataKey] = "Reduce-only close icin onay kutusunu isaretleyin.";
            return RedirectToAction(nameof(Index));
        }

        if (!Guid.TryParse(botId, out var parsedBotId) ||
            !Guid.TryParse(accountScope, out var parsedAccountScope) ||
            normalizedSymbol is null)
        {
            await WriteManualCloseAuditAsync(
                userId,
                "User.Positions.ManualCloseBlocked",
                normalizedSymbol,
                "Manual close blocked because the selected position scope could not be parsed.",
                "Blocked",
                cancellationToken);
            TempData[ManualCloseErrorTempDataKey] = "Gecersiz pozisyon secimi.";
            return RedirectToAction(nameof(Index));
        }

        var snapshot = await userDashboardPortfolioReadModelService.GetSnapshotAsync(userId, cancellationToken);
        var matchingPosition = snapshot.Positions.FirstOrDefault(
            row => row.IsExchangePosition &&
                   row.CanManualClose &&
                   row.ManualCloseBotId == parsedBotId &&
                   row.ExchangeAccountId == parsedAccountScope &&
                   string.Equals(row.Symbol, normalizedSymbol, StringComparison.OrdinalIgnoreCase));

        if (matchingPosition is null)
        {
            await WriteManualCloseAuditAsync(
                userId,
                "User.Positions.ManualCloseBlocked",
                normalizedSymbol,
                "Manual close blocked because the position scope does not belong to the authenticated user or is not closable.",
                "Blocked",
                cancellationToken);
            TempData[ManualCloseErrorTempDataKey] = "Secilen pozisyon icin manuel close kullanilamiyor.";
            return RedirectToAction(nameof(Index));
        }

        var result = await adminManualCloseService.CloseAsync(
            new AdminManualCloseRequest(
                parsedBotId,
                parsedAccountScope,
                normalizedSymbol,
                userId,
                $"user:{userId}",
                HttpContext.TraceIdentifier),
            cancellationToken);

        TempData[result.IsSuccess ? ManualCloseSuccessTempDataKey : ManualCloseErrorTempDataKey] = result.UserMessage;
        await WriteManualCloseAuditAsync(
            userId,
            result.IsSuccess ? "User.Positions.ManualClose" : "User.Positions.ManualCloseBlocked",
            normalizedSymbol,
            BuildManualCloseAuditContext(normalizedSymbol, result),
            result.IsSuccess ? "Allowed" : "Blocked",
            cancellationToken,
            result.Order?.ExecutionEnvironment.ToString() ?? matchingPosition.EnvironmentLabel ?? "BinanceTestnet");

        return RedirectToAction(nameof(Index));
    }

    private async Task<UserDashboardPortfolioSnapshot> GetPortfolioSnapshotAsync(CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();

        if (string.IsNullOrWhiteSpace(userId))
        {
            return new UserDashboardPortfolioSnapshot(
                0,
                "Henüz senkron yok",
                "neutral",
                null,
                0m,
                0m,
                0m,
                "PnL snapshot unavailable.",
                Array.Empty<UserDashboardBalanceSnapshot>(),
                Array.Empty<UserDashboardPositionSnapshot>(),
                Array.Empty<UserDashboardTradeHistoryRowSnapshot>());
        }

        return await userDashboardPortfolioReadModelService.GetSnapshotAsync(userId, cancellationToken);
    }

    private string? GetCurrentUserId()
    {
        return HttpContext?.User?.FindFirstValue(ClaimTypes.NameIdentifier);
    }

    private async Task WriteManualCloseAuditAsync(
        string userId,
        string action,
        string? symbol,
        string context,
        string outcome,
        CancellationToken cancellationToken,
        string environment = "BinanceTestnet")
    {
        await auditLogService.WriteAsync(
            new AuditLogWriteRequest(
                $"user:{userId}",
                action,
                $"Position/{symbol ?? "n/a"}",
                Truncate(context, 1024),
                HttpContext.TraceIdentifier,
                outcome,
                environment),
            cancellationToken);
    }

    private static string? NormalizeSymbol(string? symbol)
    {
        var normalizedSymbol = symbol?.Trim();
        return string.IsNullOrWhiteSpace(normalizedSymbol)
            ? null
            : normalizedSymbol.ToUpperInvariant();
    }

    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Length <= maxLength
            ? value
            : value[..maxLength];
    }

    private static string BuildManualCloseAuditContext(string symbol, AdminManualCloseResult result)
    {
        var orderState = result.Order?.State.ToString() ?? "n/a";
        var submittedToBroker = result.Order?.SubmittedToBroker ?? false;
        return $"ManualClose=True; Symbol={symbol}; OutcomeCode={result.OutcomeCode}; OrderState={orderState}; SubmittedToBroker={submittedToBroker}; ReduceOnly=True; ExitSource=Manual";
    }
}
