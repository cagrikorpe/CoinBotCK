using System.Security.Claims;
using CoinBot.Application.Abstractions.Exchange;
using CoinBot.Application.Abstractions.Execution;
using CoinBot.Application.Abstractions.Settings;
using System.Text.Json;
using CoinBot.Application.Abstractions.Mfa;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Execution;
using CoinBot.Infrastructure.Exchange;
using CoinBot.Web.ViewModels.Settings;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace CoinBot.Web.Controllers;

[Authorize]
public sealed class SettingsController : Controller
{
    private const string RecoveryCodesTempDataKey = "MfaRecoveryCodes";
    private readonly IMfaManagementService mfaManagementService;
    private readonly IUserSettingsService userSettingsService;
    private readonly IBinanceTimeSyncService binanceTimeSyncService;
    private readonly IDataLatencyCircuitBreaker dataLatencyCircuitBreaker;
    private readonly DataLatencyGuardOptions dataLatencyGuardOptions;
    private readonly BinancePrivateDataOptions privateDataOptions;

    public SettingsController(
        IMfaManagementService mfaManagementService,
        IUserSettingsService userSettingsService,
        IBinanceTimeSyncService binanceTimeSyncService,
        IDataLatencyCircuitBreaker dataLatencyCircuitBreaker,
        IOptions<DataLatencyGuardOptions> dataLatencyGuardOptions,
        IOptions<BinancePrivateDataOptions> privateDataOptions)
    {
        this.mfaManagementService = mfaManagementService;
        this.userSettingsService = userSettingsService;
        this.binanceTimeSyncService = binanceTimeSyncService;
        this.dataLatencyCircuitBreaker = dataLatencyCircuitBreaker;
        this.dataLatencyGuardOptions = dataLatencyGuardOptions.Value;
        this.privateDataOptions = privateDataOptions.Value;
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

        return View(await BuildSettingsViewModelAsync(snapshot, cancellationToken: cancellationToken));
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
            return View(await BuildSettingsViewModelAsync(snapshot, form, cancellationToken));
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
        return View(await BuildSettingsViewModelAsync(refreshedSnapshot ?? snapshot, form, cancellationToken));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RefreshClockDrift(CancellationToken cancellationToken)
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

        var refreshedSnapshot = await binanceTimeSyncService.GetSnapshotAsync(forceRefresh: true, cancellationToken);

        if (string.Equals(refreshedSnapshot.StatusCode, "Synchronized", StringComparison.OrdinalIgnoreCase))
        {
            TempData["SettingsSuccess"] =
                $"Binance server time sync yenilendi. Son probe drift {refreshedSnapshot.ClockDriftMilliseconds?.ToString() ?? "n/a"} ms. Market heartbeat drift guard ayrı değerlendirilir.";
        }
        else
        {
            TempData["SettingsError"] =
                refreshedSnapshot.FailureReason ??
                "Binance server time sync yenilenemedi. Son başarılı offset kullanılmaya devam ediyor.";
        }

        return RedirectToAction(nameof(Index));
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

    private async Task<SettingsIndexViewModel> BuildSettingsViewModelAsync(
        UserSettingsSnapshot snapshot,
        TimeZoneSettingsInputModel? formOverride = null,
        CancellationToken cancellationToken = default)
    {
        var timeZoneInfo = ResolveTimeZone(snapshot.PreferredTimeZoneId);
        var clockDrift = snapshot.BinanceTimeSync;
        var latencySnapshot = await dataLatencyCircuitBreaker.GetSnapshotAsync(HttpContext.TraceIdentifier, cancellationToken);
        var clockDriftThresholdMilliseconds = checked(dataLatencyGuardOptions.ClockDriftThresholdSeconds * 1000);

        return new SettingsIndexViewModel
        {
            Form = formOverride ?? new TimeZoneSettingsInputModel
            {
                PreferredTimeZoneId = snapshot.PreferredTimeZoneId
            },
            TimeZoneOptions = snapshot.TimeZoneOptions
                .Select(option => new TimeZoneOptionViewModel(option.TimeZoneId, option.DisplayName))
                .ToArray(),
            ClockDrift = new ClockDriftInfoViewModel(
                snapshot.PreferredTimeZoneId,
                snapshot.PreferredTimeZoneDisplayName,
                FormatInTimeZone(clockDrift.LocalAppTimeUtc, timeZoneInfo),
                FormatInTimeZone(clockDrift.ExchangeServerTimeUtc, timeZoneInfo),
                $"{clockDrift.OffsetMilliseconds} ms",
                clockDrift.ClockDriftMilliseconds is int clockDriftMilliseconds
                    ? $"{clockDriftMilliseconds} ms"
                    : "Henüz yok",
                FormatInTimeZone(clockDrift.LastSynchronizedAtUtc, timeZoneInfo),
                clockDrift.StatusCode,
                clockDrift.FailureReason,
                clockDrift.RoundTripMilliseconds is int roundTripMilliseconds
                    ? $"{roundTripMilliseconds} ms"
                    : "Henüz yok",
                $"{privateDataOptions.ServerTimeSyncRefreshSeconds} sn"),
            DriftGuard = new MarketDriftGuardInfoViewModel(
                $"{clockDriftThresholdMilliseconds} ms",
                $"{latencySnapshot.StateCode} • {(latencySnapshot.ExecutionFlowBlocked ? "Execution blocked" : "Execution open")}",
                BuildGuardReason(latencySnapshot, clockDriftThresholdMilliseconds),
                FormatInTimeZone(latencySnapshot.LatestHeartbeatReceivedAtUtc, timeZoneInfo),
                FormatInTimeZone(latencySnapshot.LatestDataTimestampAtUtc, timeZoneInfo),
                latencySnapshot.LatestDataAgeMilliseconds is int latestDataAgeMilliseconds
                    ? $"{latestDataAgeMilliseconds} ms"
                    : "Henüz yok",
                latencySnapshot.LatestClockDriftMilliseconds is int latestClockDriftMilliseconds
                    ? $"{latestClockDriftMilliseconds} ms"
                    : "Henüz yok",
                FormatInTimeZone(latencySnapshot.LastStateChangedAtUtc, timeZoneInfo),
                "Market-data heartbeat (binance:kline)",
                BuildRetryExpectation(latencySnapshot)),
            SuccessMessage = null,
            ErrorMessage = null
        };
    }

    private static string BuildGuardReason(DegradedModeSnapshot snapshot, int clockDriftThresholdMilliseconds)
    {
        return snapshot.ReasonCode switch
        {
            DegradedModeReasonCode.None when snapshot.IsNormal =>
                $"Guard normal. Threshold {clockDriftThresholdMilliseconds} ms, latest heartbeat drift {(snapshot.LatestClockDriftMilliseconds?.ToString() ?? "n/a")} ms.",
            DegradedModeReasonCode.ClockDriftExceeded =>
                $"Clock drift block aktif. Market-data heartbeat drift {(snapshot.LatestClockDriftMilliseconds?.ToString() ?? "n/a")} ms, threshold {clockDriftThresholdMilliseconds} ms.",
            DegradedModeReasonCode.MarketDataLatencyBreached or DegradedModeReasonCode.MarketDataLatencyCritical =>
                $"Market-data freshness guard aktif. Data age {(snapshot.LatestDataAgeMilliseconds?.ToString() ?? "n/a")} ms, latest heartbeat drift {(snapshot.LatestClockDriftMilliseconds?.ToString() ?? "n/a")} ms.",
            DegradedModeReasonCode.MarketDataUnavailable =>
                "Market-data heartbeat henüz güvenli kabul edilecek kadar gelmedi.",
            _ =>
                $"Guard reason {snapshot.ReasonCode}. Latest heartbeat drift {(snapshot.LatestClockDriftMilliseconds?.ToString() ?? "n/a")} ms."
        };
    }

    private static string BuildRetryExpectation(DegradedModeSnapshot snapshot)
    {
        return snapshot.ReasonCode switch
        {
            DegradedModeReasonCode.ClockDriftExceeded =>
                "Server-time refresh yalnız signed REST offset'ini yeniler. Yeni order için market-data heartbeat drift'inin threshold altına inmesi gerekir.",
            DegradedModeReasonCode.MarketDataLatencyBreached or DegradedModeReasonCode.MarketDataLatencyCritical or DegradedModeReasonCode.MarketDataUnavailable =>
                "Retry öncesi fresh kline heartbeat beklenmelidir; yalnız timezone değişimi veya server-time refresh bu blokları tek başına kaldırmaz.",
            _ =>
                "Server-time refresh sonrası signed REST timestamp yeniden senkronlanır. Guard normal ise sonraki retry order path'e ilerleyebilir."
        };
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

    private static string FormatInTimeZone(DateTime? utcTimestamp, TimeZoneInfo timeZoneInfo)
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
