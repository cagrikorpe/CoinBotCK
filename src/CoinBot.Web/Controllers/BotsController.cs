using System.Security.Claims;
using CoinBot.Application.Abstractions.Bots;
using CoinBot.Application.Abstractions.Settings;
using CoinBot.Contracts.Common;
using CoinBot.Web.ViewModels.Bots;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CoinBot.Web.Controllers;

[Authorize(Policy = ApplicationPolicies.TradeOperations)]
public class BotsController(
    IBotManagementService botManagementService,
    IBotPilotControlService botPilotControlService,
    IUserSettingsService userSettingsService) : Controller
{
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();

        if (userId is null)
        {
            return Challenge();
        }

        var snapshot = await botManagementService.GetPageAsync(userId, cancellationToken);
        var settingsSnapshot = await userSettingsService.GetAsync(userId, cancellationToken);
        return View(MapPage(snapshot, ResolveTimeZone(settingsSnapshot?.PreferredTimeZoneId)));
    }

    [HttpGet("Bots/Create")]
    public async Task<IActionResult> Create(CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();

        if (userId is null)
        {
            return Challenge();
        }

        var snapshot = await botManagementService.GetCreateEditorAsync(userId, cancellationToken);
        return View("Editor", MapEditor(snapshot, isEditMode: false));
    }

    [HttpPost("Bots/Create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(BotManagementInputModel form, CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();

        if (userId is null)
        {
            return Challenge();
        }

        if (!ModelState.IsValid)
        {
            var invalidSnapshot = await botManagementService.GetCreateEditorAsync(userId, cancellationToken);
            return View("Editor", MapEditor(invalidSnapshot, isEditMode: false, form));
        }

        var result = await botManagementService.CreateAsync(
            userId,
            MapCommand(form),
            $"user:{userId}",
            HttpContext.TraceIdentifier,
            cancellationToken);

        if (result.IsSuccessful)
        {
            TempData["BotControlSuccess"] = "Pilot bot kaydedildi.";
            return RedirectToAction(nameof(Index));
        }

        if (result.WasPersisted && result.BotId.HasValue)
        {
            TempData["BotControlError"] = result.FailureReason ?? "Bot kaydedildi ancak etkinleştirilemedi.";
            return RedirectToAction(nameof(Edit), new { botId = result.BotId.Value });
        }

        ModelState.AddModelError(string.Empty, result.FailureReason ?? "Bot kaydedilemedi.");
        var snapshot = await botManagementService.GetCreateEditorAsync(userId, cancellationToken);
        return View("Editor", MapEditor(snapshot, isEditMode: false, form));
    }

    [HttpGet("Bots/{botId:guid}/Edit")]
    public async Task<IActionResult> Edit(Guid botId, CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();

        if (userId is null)
        {
            return Challenge();
        }

        var snapshot = await botManagementService.GetEditEditorAsync(userId, botId, cancellationToken);

        if (snapshot is null)
        {
            return NotFound();
        }

        return View("Editor", MapEditor(snapshot, isEditMode: true));
    }

    [HttpPost("Bots/{botId:guid}/Edit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(Guid botId, BotManagementInputModel form, CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();

        if (userId is null)
        {
            return Challenge();
        }

        var snapshot = await botManagementService.GetEditEditorAsync(userId, botId, cancellationToken);

        if (snapshot is null)
        {
            return NotFound();
        }

        if (!ModelState.IsValid)
        {
            return View("Editor", MapEditor(snapshot, isEditMode: true, form));
        }

        var result = await botManagementService.UpdateAsync(
            userId,
            botId,
            MapCommand(form),
            $"user:{userId}",
            HttpContext.TraceIdentifier,
            cancellationToken);

        if (result.IsSuccessful)
        {
            TempData["BotControlSuccess"] = "Pilot bot guncellendi.";
            return RedirectToAction(nameof(Index));
        }

        if (string.Equals(result.FailureCode, "BotNotFound", StringComparison.Ordinal))
        {
            return NotFound();
        }

        ModelState.AddModelError(string.Empty, result.FailureReason ?? "Bot guncellenemedi.");
        var refreshedSnapshot = await botManagementService.GetEditEditorAsync(userId, botId, cancellationToken);

        if (refreshedSnapshot is null)
        {
            return NotFound();
        }

        return View("Editor", MapEditor(refreshedSnapshot, isEditMode: true, form));
    }

    [HttpPost("Bots/{botId:guid}/enabled")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetEnabled(Guid botId, [FromForm] bool isEnabled, CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();

        if (userId is null)
        {
            return Challenge();
        }

        var result = await botPilotControlService.SetEnabledAsync(
            userId,
            botId,
            isEnabled,
            $"user:{userId}",
            HttpContext.TraceIdentifier,
            cancellationToken);

        TempData[result.IsSuccessful ? "BotControlSuccess" : "BotControlError"] = result.IsSuccessful
            ? (isEnabled ? "Pilot bot etkinlestirildi." : "Pilot bot durduruldu.")
            : result.FailureReason ?? "Bot durumu guncellenemedi.";

        return RedirectToAction(nameof(Index));
    }

    private string? GetCurrentUserId()
    {
        return User.FindFirstValue(ClaimTypes.NameIdentifier);
    }

    private static BotManagementSaveCommand MapCommand(BotManagementInputModel form)
    {
        return new BotManagementSaveCommand(
            form.Name,
            form.StrategyKey,
            form.Symbol,
            form.Quantity,
            form.ExchangeAccountId,
            form.Leverage,
            form.MarginType,
            form.IsEnabled);
    }

    private static BotManagementIndexViewModel MapPage(BotManagementPageSnapshot snapshot, TimeZoneInfo timeZoneInfo)
    {
        var rows = snapshot.Bots
            .Select(item => new BotManagementRowViewModel(
                item.BotId,
                item.Name,
                item.StrategyDisplayName ?? item.StrategyKey,
                item.StrategyKey,
                item.HasPublishedStrategyVersion,
                item.Symbol,
                item.Quantity?.ToString("0.##################") ?? "Auto",
                item.ExchangeAccountDisplayName ?? "Auto",
                item.ExchangeAccountIsActive,
                item.ExchangeAccountIsWritable,
                item.Leverage?.ToString("0.##################") ?? "1",
                item.MarginType ?? "ISOLATED",
                item.IsEnabled,
                item.OpenOrderCount,
                item.OpenPositionCount,
                item.LastJobStatus ?? "Pending",
                item.LastJobErrorCode,
                item.LastExecutionState ?? "N/A",
                item.LastExecutionFailureCode,
                item.LastExecutionBlockDetail,
                ResolveExecutionStageText(item.LastExecutionRejectionStage),
                $"Submitted: {(item.LastExecutionSubmittedToBroker ? "Yes" : "No")}",
                $"Retry: {(item.LastExecutionRetryEligible ? "Eligible" : "No")} | Cooldown: {(item.LastExecutionCooldownApplied ? "Applied" : "No")}",
                $"ReduceOnly: {(item.LastExecutionReduceOnly ? "Yes" : "No")} | SL: {(item.LastExecutionStopLossAttached ? "Yes" : "No")} | TP: {(item.LastExecutionTakeProfitAttached ? "Yes" : "No")}",
                ResolveExecutionTransitionText(item.LastExecutionTransitionCode),
                ResolveExecutionCorrelationText(item.LastExecutionTransitionCorrelationId),
                ResolveExecutionClientOrderText(item.LastExecutionClientOrderId),
                item.LastExecutionDuplicateSuppressed ? "Duplicate suppressed" : null,
                item.CooldownBlockedUntilUtc.HasValue,
                item.CooldownBlockedUntilUtc.HasValue
                    ? FormatTimestamp(item.CooldownBlockedUntilUtc, timeZoneInfo)
                    : null,
                item.CooldownRemainingSeconds.HasValue
                    ? $"{item.CooldownRemainingSeconds.Value} sn"
                    : null,
                FormatTimestamp(item.UpdatedAtUtc, timeZoneInfo),
                FormatTimestamp(item.LastExecutionUpdatedAtUtc, timeZoneInfo),
                ResolveMarketDataBadgeText(item),
                item.LastExecutionLastCandleAtUtc.HasValue
                    ? FormatTimestamp(item.LastExecutionLastCandleAtUtc, timeZoneInfo)
                    : null,
                item.LastExecutionDataAgeMilliseconds.HasValue
                    ? $"{item.LastExecutionDataAgeMilliseconds.Value} ms"
                    : null,
                item.LastExecutionContinuityState,
                item.LastExecutionContinuityGapCount.HasValue
                    ? item.LastExecutionContinuityGapCount.Value.ToString()
                    : null,
                ResolveAffectedMarketText(item.LastExecutionAffectedSymbol, item.LastExecutionAffectedTimeframe),
                item.LastExecutionStaleReason,
                ResolveDecisionText(item.LastExecutionDecisionOutcome, item.LastExecutionDecisionReasonType),
                string.IsNullOrWhiteSpace(item.LastExecutionDecisionReasonCode)
                    ? null
                    : $"ReasonCode: {item.LastExecutionDecisionReasonCode}",
                item.LastExecutionDecisionSummary,
                item.LastExecutionDecisionAtUtc.HasValue
                    ? FormatTimestamp(item.LastExecutionDecisionAtUtc, timeZoneInfo)
                    : null,
                item.LastExecutionStaleThresholdMilliseconds.HasValue
                    ? $"{item.LastExecutionStaleThresholdMilliseconds.Value} ms"
                    : null,
                item.LastExecutionContinuityGapStartedAtUtc.HasValue
                    ? FormatTimestamp(item.LastExecutionContinuityGapStartedAtUtc, timeZoneInfo)
                    : null,
                item.LastExecutionContinuityGapLastSeenAtUtc.HasValue
                    ? FormatTimestamp(item.LastExecutionContinuityGapLastSeenAtUtc, timeZoneInfo)
                    : null,
                item.LastExecutionContinuityRecoveredAtUtc.HasValue
                    ? FormatTimestamp(item.LastExecutionContinuityRecoveredAtUtc, timeZoneInfo)
                    : null))
            .ToArray();

        return new BotManagementIndexViewModel(rows);
    }

    private static BotManagementEditorViewModel MapEditor(
        BotManagementEditorSnapshot snapshot,
        bool isEditMode,
        BotManagementInputModel? formOverride = null)
    {
        var form = formOverride ?? new BotManagementInputModel
        {
            Name = snapshot.Draft.Name,
            StrategyKey = snapshot.Draft.StrategyKey,
            Symbol = snapshot.Draft.Symbol,
            Quantity = snapshot.Draft.Quantity,
            ExchangeAccountId = snapshot.Draft.ExchangeAccountId,
            Leverage = snapshot.Draft.Leverage ?? 1m,
            MarginType = snapshot.Draft.MarginType,
            IsEnabled = snapshot.Draft.IsEnabled
        };

        return new BotManagementEditorViewModel(
            snapshot.BotId,
            isEditMode,
            form,
            snapshot.SymbolOptions
                .Select(option => new BotManagementOptionViewModel(option, option))
                .ToArray(),
            snapshot.StrategyOptions
                .Select(option => new BotManagementOptionViewModel(
                    option.StrategyKey,
                    option.HasPublishedVersion
                        ? $"{option.DisplayName} ({option.StrategyKey})"
                        : $"{option.DisplayName} ({option.StrategyKey}) - yayinlanmis versiyon yok"))
                .ToArray(),
            snapshot.ExchangeAccountOptions
                .Select(option => new BotManagementOptionViewModel(
                    option.ExchangeAccountId.ToString(),
                    $"{option.DisplayName} - {(option.IsActive ? "Active" : "Inactive")} / {(option.IsWritable ? "Writable" : "ReadOnly")}"))
                .ToArray());
    }

    private static string? ResolveMarketDataBadgeText(BotManagementBotSnapshot snapshot)
    {
        return snapshot.LastExecutionStaleReason switch
        {
            "Clock drift exceeded" => "Data latency high",
            "Market data stale" => "Market data stale",
            "Market data unavailable" => "Market data unavailable",
            "Continuity gap detected" or "Duplicate candle detected" or "Out-of-order candle detected" => "Continuity guard active",
            _ => null
        };
    }

    private static string? ResolveAffectedMarketText(string? symbol, string? timeframe)
    {
        if (string.IsNullOrWhiteSpace(symbol) && string.IsNullOrWhiteSpace(timeframe))
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(symbol)
            ? timeframe
            : string.IsNullOrWhiteSpace(timeframe)
                ? symbol
                : $"{symbol} / {timeframe}";
    }

    private static string? ResolveDecisionText(string? decisionOutcome, string? decisionReasonType)
    {
        if (string.IsNullOrWhiteSpace(decisionOutcome))
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(decisionReasonType) || string.Equals(decisionReasonType, "Allow", StringComparison.Ordinal)
            ? $"Decision: {decisionOutcome.Trim()}"
            : $"Decision: {decisionOutcome.Trim()} / {decisionReasonType.Trim()}";
    }

    private static string? ResolveExecutionStageText(string? rejectionStage)
    {
        if (string.IsNullOrWhiteSpace(rejectionStage) ||
            string.Equals(rejectionStage, "None", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return $"Stage: {rejectionStage.Trim()}";
    }

    private static string? ResolveExecutionTransitionText(string? transitionCode)
    {
        return string.IsNullOrWhiteSpace(transitionCode)
            ? null
            : $"Transition: {transitionCode.Trim()}";
    }

    private static string? ResolveExecutionCorrelationText(string? correlationId)
    {
        return string.IsNullOrWhiteSpace(correlationId)
            ? null
            : $"Correlation: {correlationId.Trim()}";
    }

    private static string? ResolveExecutionClientOrderText(string? clientOrderId)
    {
        return string.IsNullOrWhiteSpace(clientOrderId)
            ? null
            : $"ClientOrderId: {clientOrderId.Trim()}";
    }

    private static TimeZoneInfo ResolveTimeZone(string? timeZoneId)
    {
        if (!string.IsNullOrWhiteSpace(timeZoneId))
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId.Trim());
            }
            catch (TimeZoneNotFoundException)
            {
            }
            catch (InvalidTimeZoneException)
            {
            }
        }

        return TimeZoneInfo.Utc;
    }

    private static string FormatTimestamp(DateTime? utcTimestamp, TimeZoneInfo timeZoneInfo)
    {
        if (!utcTimestamp.HasValue)
        {
            return "Henüz yok";
        }

        var normalizedUtcTimestamp = utcTimestamp.Value.Kind == DateTimeKind.Utc
            ? utcTimestamp.Value
            : DateTime.SpecifyKind(utcTimestamp.Value, DateTimeKind.Utc);
        var localTimestamp = TimeZoneInfo.ConvertTimeFromUtc(normalizedUtcTimestamp, timeZoneInfo);
        return $"{localTimestamp:yyyy-MM-dd HH:mm:ss} {timeZoneInfo.StandardName}";
    }
}


