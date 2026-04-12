using System.Globalization;
using CoinBot.Application.Abstractions.Administration;
using CoinBot.Application.Abstractions.Execution;
using CoinBot.Domain.Enums;

namespace CoinBot.Web.ViewModels.Admin;

public sealed record AdminSuperAdminPrimaryFlowViewModel(
    AdminSuperAdminFlowSectionViewModel Setup,
    AdminSuperAdminFlowSectionViewModel Activation,
    AdminSuperAdminFlowSectionViewModel Monitoring,
    AdminSuperAdminFlowSectionViewModel Advanced);

public sealed record AdminSuperAdminFlowSectionViewModel(
    string Key,
    string Label,
    string StatusLabel,
    string Tone,
    string Summary,
    string PrimaryMessage,
    bool IsVisible,
    bool IsAccessible,
    IReadOnlyCollection<AdminSuperAdminFlowItemViewModel> Items,
    IReadOnlyCollection<AdminSuperAdminFlowActionViewModel> Actions);

public sealed record AdminSuperAdminFlowItemViewModel(
    string Key,
    string Label,
    string Value,
    string Tone,
    string Summary,
    string? Href = null);

public sealed record AdminSuperAdminFlowActionViewModel(
    string Key,
    string Label,
    bool IsEnabled,
    string BlockedReason,
    string Summary);

public static class AdminSuperAdminPrimaryFlowComposer
{
    public static AdminSuperAdminPrimaryFlowViewModel CreateAccessDenied(DateTime evaluatedAtUtc)
    {
        var blockedSection = new AdminSuperAdminFlowSectionViewModel(
            "blocked",
            "Blocked",
            "Blocked",
            "critical",
            "Bu sade super admin akisi yalnizca Super Admin rolu ile acilir.",
            "Super Admin gerekli",
            false,
            false,
            new[]
            {
                new AdminSuperAdminFlowItemViewModel(
                    "access",
                    "Erisim",
                    "Blocked",
                    "critical",
                    $"Fail-closed. Refreshed {FormatUtc(evaluatedAtUtc)}")
            },
            Array.Empty<AdminSuperAdminFlowActionViewModel>());

        return new AdminSuperAdminPrimaryFlowViewModel(
            blockedSection with { Key = "setup", Label = "Sistem Kurulumu" },
            blockedSection with { Key = "activation", Label = "Sistemi Aktiflestir" },
            blockedSection with { Key = "monitoring", Label = "Sistemi Izle" },
            blockedSection with { Key = "advanced", Label = "Gelismis" });
    }

    public static AdminSuperAdminPrimaryFlowViewModel Compose(
        AdminActivationControlCenterViewModel activationControlCenter,
        AdminOperationsRuntimeHealthCenterViewModel runtimeCenter,
        AdminOperationsExchangeGovernanceCenterViewModel exchangeCenter,
        AdminBotOperationsPageSnapshot botOperationsSnapshot,
        GlobalExecutionSwitchSnapshot executionSnapshot,
        GlobalSystemStateSnapshot globalSystemStateSnapshot,
        bool canRefreshOperationalState,
        DateTime evaluatedAtUtc)
    {
        var workerSignal = runtimeCenter.Signals.FirstOrDefault(item => string.Equals(item.Key, "worker-health", StringComparison.OrdinalIgnoreCase));
        var exchangeReady = HasReadyExchange(exchangeCenter, activationControlCenter.CurrentModeLabel);
        var workerReady = workerSignal is not null && string.Equals(workerSignal.Tone, "healthy", StringComparison.OrdinalIgnoreCase);
        var emergencyActive = IsEmergencyActive(activationControlCenter, globalSystemStateSnapshot);
        var setupReady = exchangeReady && workerReady && activationControlCenter.IsActivatable;
        var activationStatus = ResolveActivationStatus(activationControlCenter, emergencyActive);
        var monitoringStatus = ResolveMonitoringStatus(executionSnapshot, emergencyActive, workerReady, exchangeReady);
        var setupMessage = BuildSetupMessage(activationControlCenter, exchangeReady, workerReady);
        var activationMessage = BuildActivationMessage(activationControlCenter, exchangeReady, workerReady, emergencyActive);
        var monitoringMessage = BuildMonitoringMessage(executionSnapshot, activationControlCenter, runtimeCenter, emergencyActive, exchangeReady, workerReady);
        var lastErrorSummary = ResolveLastErrorSummary(activationControlCenter, runtimeCenter);
        var lastStopSummary = ResolveLastStopSummary(executionSnapshot, globalSystemStateSnapshot, activationControlCenter);
        var runningBotCount = CountRunningBots(botOperationsSnapshot);
        var monitoringDisplayStatus = ResolveMonitoringDisplayStatus(monitoringStatus);
        var lastErrorDisplay = ResolveLastErrorDisplay(lastErrorSummary);
        var lastStopDisplay = ResolveLastStopDisplay(lastStopSummary);
        var lastOperationTimeLabel = ResolveLastOperationTimeLabel(botOperationsSnapshot, runningBotCount);

        return new AdminSuperAdminPrimaryFlowViewModel(
            new AdminSuperAdminFlowSectionViewModel(
                "setup",
                "Sistem Kurulumu",
                setupReady ? "Hazir" : "Eksik",
                setupReady ? "healthy" : "critical",
                setupReady
                    ? "Ortam secimi, exchange baglantisi ve worker durumu temiz gorunuyor."
                    : "Kurulum eksigi var; tek satir blocker mesajini giderin.",
                setupMessage,
                true,
                true,
                new[]
                {
                    new AdminSuperAdminFlowItemViewModel("environment", "Ortam", activationControlCenter.CurrentModeLabel, activationControlCenter.CurrentModeLabel.Equals("Live", StringComparison.OrdinalIgnoreCase) ? "warning" : "info", activationControlCenter.CurrentModeSummary),
                    new AdminSuperAdminFlowItemViewModel("exchange", "Exchange hazir mi", exchangeReady ? "Hazir" : "Eksik", exchangeReady ? "healthy" : "critical", exchangeReady ? "Secili ortam icin exchange hesabi hazir gorunuyor." : ResolveExchangeBlockedSummary(activationControlCenter.CurrentModeLabel)),
                    new AdminSuperAdminFlowItemViewModel("worker", "Worker hazir mi", workerReady ? "Hazir" : "Eksik", workerReady ? "healthy" : "critical", workerReady ? "Worker heartbeat gorunuyor." : "Worker calismiyor.")
                },
                BuildSetupActions(canRefreshOperationalState)),
            new AdminSuperAdminFlowSectionViewModel(
                "activation",
                "Sistemi Aktiflestir",
                activationStatus,
                activationStatus switch
                {
                    "Aktif" => "healthy",
                    "Hazir" => "warning",
                    "Acil durdurma aktif" => "critical",
                    _ => "critical"
                },
                activationStatus switch
                {
                    "Aktif" => "Sistem aktif. Gerekirse guvenli kapatma veya acil durdur kullanin.",
                    "Hazir" => "Aktivasyon verilebilir; siradaki adim sistemi aktif etmektir.",
                    "Acil durdurma aktif" => "Acil durdurma aktifken aktivasyon verilmez.",
                    _ => "Aktivasyon once eksikleri kapatip tekrar degerlendirme ister."
                },
                activationMessage,
                true,
                true,
                new[]
                {
                    new AdminSuperAdminFlowItemViewModel("environment", "Ortam ozeti", activationControlCenter.CurrentModeLabel, activationControlCenter.CurrentModeLabel.Equals("Live", StringComparison.OrdinalIgnoreCase) ? "warning" : "info", activationControlCenter.CurrentModeSummary),
                    new AdminSuperAdminFlowItemViewModel("exchange", "Exchange hazir mi", exchangeReady ? "Hazir" : "Eksik", exchangeReady ? "healthy" : "critical", exchangeReady ? "Exchange baglantisi hazir." : ResolveExchangeBlockedSummary(activationControlCenter.CurrentModeLabel)),
                    new AdminSuperAdminFlowItemViewModel("worker", "Worker hazir mi", workerReady ? "Hazir" : "Eksik", workerReady ? "healthy" : "critical", workerReady ? "Worker heartbeat gorunuyor." : "Worker calismiyor."),
                    new AdminSuperAdminFlowItemViewModel("trade-master", "TradeMaster acik mi", ResolveTradeMasterValue(activationControlCenter), ResolveSwitchTone(activationControlCenter, "trade-master", "critical"), ResolveSwitchSummary(activationControlCenter, "trade-master", "TradeMaster kapali.")),
                    new AdminSuperAdminFlowItemViewModel("system-active", "Sistem aktif mi", activationControlCenter.IsCurrentlyActive ? "Aktif" : "Durduruldu", activationControlCenter.IsCurrentlyActive ? "healthy" : "warning", activationControlCenter.IsCurrentlyActive ? "Sistem su anda aktif." : "Sistem su anda kapali.")
                },
                BuildActivationActions(activationControlCenter, exchangeReady, workerReady, emergencyActive, canRefreshOperationalState)),
            new AdminSuperAdminFlowSectionViewModel(
                "monitoring",
                "Sistemi Izle",
                monitoringDisplayStatus,
                monitoringStatus switch
                {
                    "Aktif" => "healthy",
                    "Durduruldu" => "warning",
                    "Acil durdurma aktif" => "critical",
                    _ => "critical"
                },
                monitoringStatus switch
                {
                    "Aktif" => "Sistem aktif. Sadece kisa durum ozeti gosterilir.",
                    "Durduruldu" => "Sistem kapali. Son hata ve durdurma nedeni gorunur.",
                    "Acil durdurma aktif" => "Acil durdurma aktif; sistem yeniden acilmaz.",
                    _ => "Sistem hazir degil."
                },
                monitoringMessage,
                true,
                true,
                new[]
                {
                    new AdminSuperAdminFlowItemViewModel("status", "Sistem aktif mi", monitoringDisplayStatus, monitoringStatus switch { "Aktif" => "healthy", "Durduruldu" => "warning", _ => "critical" }, ResolveMonitoringStatusSummary(monitoringStatus)),
                    new AdminSuperAdminFlowItemViewModel("environment", "Ortam", activationControlCenter.CurrentModeLabel, activationControlCenter.CurrentModeLabel.Equals("Live", StringComparison.OrdinalIgnoreCase) ? "warning" : "info", activationControlCenter.CurrentModeSummary),
                    new AdminSuperAdminFlowItemViewModel("running-bots", "Calisan bot sayisi", runningBotCount.ToString(CultureInfo.InvariantCulture), runningBotCount > 0 ? "healthy" : "warning", runningBotCount > 0 ? "Bot calisiyor." : "Calisan bot yok."),
                    new AdminSuperAdminFlowItemViewModel("last-error", "Son hata", lastErrorDisplay, string.Equals(lastErrorDisplay, "Son hata yok", StringComparison.OrdinalIgnoreCase) ? "healthy" : "warning", string.Equals(lastErrorDisplay, "Son hata yok", StringComparison.OrdinalIgnoreCase) ? "Sorun gorunmuyor." : "Kisa hata ozeti."),
                    new AdminSuperAdminFlowItemViewModel("last-stop", "Son durdurma nedeni", lastStopDisplay, string.Equals(lastStopDisplay, "Son durdurma nedeni yok", StringComparison.OrdinalIgnoreCase) ? "healthy" : "warning", string.Equals(lastStopDisplay, "Son durdurma nedeni yok", StringComparison.OrdinalIgnoreCase) ? "Durdurma nedeni yok." : "Kisa durdurma nedeni."),
                    new AdminSuperAdminFlowItemViewModel("last-operation", "Son islem zamani", lastOperationTimeLabel, runningBotCount > 0 ? "info" : "warning", runningBotCount > 0 ? "Son izleme kaydi." : "Son islem yok.")
                },
                BuildMonitoringActions(canRefreshOperationalState)),
            new AdminSuperAdminFlowSectionViewModel(
                "advanced",
                "Gelismis",
                "Hazir",
                "info",
                "Teknik denetim, audit, trace ve rollout kanitlari ana akistan ayrik tutulur.",
                "Detaylar sadece gerektiginde acilir.",
                true,
                true,
                new[]
                {
                    new AdminSuperAdminFlowItemViewModel("audit", "Audit ve Trace", "Ac", "warning", "Karar zinciri ve admin aksiyonlari", "/admin/Audit"),
                    new AdminSuperAdminFlowItemViewModel("logs", "Loglar / Diagnostik", "Ac", "info", "Log ve destek aramalari", "/admin/SupportTools"),
                    new AdminSuperAdminFlowItemViewModel("incidents", "Incidents", "Ac", "warning", "Incident listesi ve detaylari", "/admin/Incidents"),
                    new AdminSuperAdminFlowItemViewModel("health", "Health detaylari", "Ac", "info", "Worker ve runtime durumu", "/admin/SystemHealth"),
                    new AdminSuperAdminFlowItemViewModel("trace", "Trace arama", "Ac", "info", "Trace ve correlation aramasi", "/admin/Search"),
                    new AdminSuperAdminFlowItemViewModel("strategies", "Stratejiler", "Ac", "info", "Template katalogu ve builder", "/admin/StrategyTemplates"),
                    new AdminSuperAdminFlowItemViewModel("strategy-builder", "Strateji Builder", "Ac", "info", "Template duzenleme yuzeyi", "/admin/StrategyBuilder"),
                    new AdminSuperAdminFlowItemViewModel("rollout", "Rollout Kanitlari", "Ac", "info", "Config ve state gecmisi", "/admin/ConfigHistory"),
                    new AdminSuperAdminFlowItemViewModel("execution-debugger", "Execution debugger", "Ac", "info", "Execution kararlarini auditte ara", "/admin/Audit?query=Execution"),
                    new AdminSuperAdminFlowItemViewModel("idempotency-rebuild", "Idempotency / rebuild", "Ac", "info", "Job ve rebuild sinyalleri", "/admin/Jobs"),
                    new AdminSuperAdminFlowItemViewModel("symbol-restrictions", "Symbol Restrictions", "Ac", "warning", "Global policy sembol bloklarini yonet", "/admin/Settings#cb_admin_settings_policy_restrictions"),
                    new AdminSuperAdminFlowItemViewModel("global-settings", "Global Ayarlar", "Ac", "info", "Teknik ayarlar ve guarded formlar", "/admin/Settings")
                },
                Array.Empty<AdminSuperAdminFlowActionViewModel>()));
    }

    private static IReadOnlyCollection<AdminSuperAdminFlowActionViewModel> BuildSetupActions(bool canRefreshOperationalState)
    {
        return
        [
            new AdminSuperAdminFlowActionViewModel("save-continue", "Kaydet ve Devam Et", true, string.Empty, "Ortam secimini kaydeder."),
            new AdminSuperAdminFlowActionViewModel("connection-test", "Baglantiyi Test Et", canRefreshOperationalState, canRefreshOperationalState ? string.Empty : "Bu rolde baglanti testi acik degil", "Baglanti ozetini yeniler.")
        ];
    }

    private static IReadOnlyCollection<AdminSuperAdminFlowActionViewModel> BuildActivationActions(
        AdminActivationControlCenterViewModel activationControlCenter,
        bool exchangeReady,
        bool workerReady,
        bool emergencyActive,
        bool canRefreshOperationalState)
    {
        var activateEnabled = activationControlCenter.IsActivatable && exchangeReady && workerReady && !activationControlCenter.IsCurrentlyActive && !emergencyActive;
        var activateBlockedReason = activateEnabled
            ? string.Empty
            : activationControlCenter.IsCurrentlyActive
                ? "Sistem zaten aktif"
                : BuildActivationMessage(activationControlCenter, exchangeReady, workerReady, emergencyActive);
        var deactivateEnabled = activationControlCenter.IsCurrentlyActive && !emergencyActive;
        var deactivateBlockedReason = deactivateEnabled
            ? string.Empty
            : emergencyActive
                ? "Acil durdurma aktif"
                : "Sistem zaten kapali";

        return
        [
            new AdminSuperAdminFlowActionViewModel("prepare", "Sistemi Hazirla", canRefreshOperationalState, canRefreshOperationalState ? string.Empty : "Bu rolde hazirlik yenilemesi acik degil", "Hazirlik ozetini yeniler."),
            new AdminSuperAdminFlowActionViewModel("activate", "Sistemi Aktif Et", activateEnabled, activateBlockedReason, "Sistemi kontrollu sekilde aktif eder."),
            new AdminSuperAdminFlowActionViewModel("deactivate", "Sistemi Kapat", deactivateEnabled, deactivateBlockedReason, "Sistemi guvenli sekilde kapatir.")
        ];
    }

    private static IReadOnlyCollection<AdminSuperAdminFlowActionViewModel> BuildMonitoringActions(bool canRefreshOperationalState)
    {
        return
        [
            new AdminSuperAdminFlowActionViewModel("refresh", "Yenile", canRefreshOperationalState, canRefreshOperationalState ? string.Empty : "Sistem hazir degil", "Izleme ozetini yeniler.")
        ];
    }

    private static bool HasReadyExchange(AdminOperationsExchangeGovernanceCenterViewModel exchangeCenter, string currentModeLabel)
    {
        var requiresLive = currentModeLabel.Equals("Live", StringComparison.OrdinalIgnoreCase);

        return exchangeCenter.Accounts.Any(account =>
            string.Equals(account.ValidationLabel, "Valid", StringComparison.OrdinalIgnoreCase) &&
            (!requiresLive
                ? account.EnvironmentLabel.Contains("Test", StringComparison.OrdinalIgnoreCase) || account.EnvironmentLabel.Contains("Demo", StringComparison.OrdinalIgnoreCase)
                : account.EnvironmentLabel.Contains("Live", StringComparison.OrdinalIgnoreCase)));
    }

    private static bool IsEmergencyActive(AdminActivationControlCenterViewModel activationControlCenter, GlobalSystemStateSnapshot globalSystemStateSnapshot)
    {
        var emergencySwitch = activationControlCenter.CriticalSwitches.FirstOrDefault(item => string.Equals(item.Key, "emergency-stop", StringComparison.OrdinalIgnoreCase));
        return string.Equals(emergencySwitch?.Value, "Active", StringComparison.OrdinalIgnoreCase) ||
               globalSystemStateSnapshot.State == GlobalSystemStateKind.FullHalt;
    }

    private static string ResolveActivationStatus(AdminActivationControlCenterViewModel activationControlCenter, bool emergencyActive)
    {
        if (emergencyActive)
        {
            return "Acil durdurma aktif";
        }

        if (activationControlCenter.IsCurrentlyActive)
        {
            return "Aktif";
        }

        return activationControlCenter.IsActivatable
            ? "Hazir"
            : "Eksik";
    }

    private static string ResolveMonitoringStatus(GlobalExecutionSwitchSnapshot executionSnapshot, bool emergencyActive, bool workerReady, bool exchangeReady)
    {
        if (emergencyActive)
        {
            return "Acil durdurma aktif";
        }

        if (executionSnapshot.IsTradeMasterArmed)
        {
            return "Aktif";
        }

        return workerReady || exchangeReady
            ? "Durduruldu"
            : "Eksik";
    }

    private static string BuildSetupMessage(AdminActivationControlCenterViewModel activationControlCenter, bool exchangeReady, bool workerReady)
    {
        if (!exchangeReady)
        {
            return ResolveExchangeBlockedSummary(activationControlCenter.CurrentModeLabel);
        }

        if (!workerReady)
        {
            return "Worker calismiyor";
        }

        return activationControlCenter.IsActivatable
            ? "Hazir"
            : ResolveActivationBlockedSummary(activationControlCenter);
    }

    private static string BuildActivationMessage(AdminActivationControlCenterViewModel activationControlCenter, bool exchangeReady, bool workerReady, bool emergencyActive)
    {
        if (emergencyActive)
        {
            return "Acil durdurma aktif";
        }

        if (!exchangeReady)
        {
            return ResolveExchangeBlockedSummary(activationControlCenter.CurrentModeLabel);
        }

        if (!workerReady)
        {
            return "Worker calismiyor";
        }

        if (!activationControlCenter.IsActivatable)
        {
            return ResolveActivationBlockedSummary(activationControlCenter);
        }

        return activationControlCenter.IsCurrentlyActive
            ? "Aktif"
            : "Hazir";
    }

    private static string BuildMonitoringMessage(GlobalExecutionSwitchSnapshot executionSnapshot, AdminActivationControlCenterViewModel activationControlCenter, AdminOperationsRuntimeHealthCenterViewModel runtimeCenter, bool emergencyActive, bool exchangeReady, bool workerReady)
    {
        if (emergencyActive)
        {
            return "Acil durdurma aktif";
        }

        if (!workerReady)
        {
            return "Worker calismiyor";
        }

        if (!exchangeReady)
        {
            return ResolveExchangeBlockedSummary(activationControlCenter.CurrentModeLabel);
        }

        return executionSnapshot.IsTradeMasterArmed
            ? "Sistem aktif"
            : (string.Equals(runtimeCenter.StatusTone, "critical", StringComparison.OrdinalIgnoreCase) ? "Eksik" : "Sistem kapali");
    }

    private static string ResolveActivationBlockedSummary(AdminActivationControlCenterViewModel activationControlCenter)
    {
        var blocker = activationControlCenter.BlockingItems.FirstOrDefault();
        if (blocker is null)
        {
            return "Sistem aktive edilmeye hazir degil";
        }

        return blocker.ReasonCode switch
        {
            "PilotActivationDisabled" => "PilotActivation kapali",
            "LiveModeApprovalMissing" => "Live secildi ama izin yok",
            "ActivationStateUnavailable" => "Sistem hazir degil",
            "ServerTimeSyncUnavailable" or "ClockDriftUnavailable" or "ClockDriftExceeded" => "Saat senkronu hazir degil",
            "DataLatencyGuardUnavailable" or "MarketDataUnavailable" or "MarketDataLatencyBreached" or "MarketDataLatencyCritical" or "CandleDataGapDetected" => "Piyasa verisi guncel degil",
            _ => "Sistem aktive edilmeye hazir degil"
        };
    }

    private static string ResolveExchangeBlockedSummary(string currentModeLabel)
    {
        return currentModeLabel.Equals("Live", StringComparison.OrdinalIgnoreCase)
            ? "Live secildi ama izin yok"
            : "Exchange bagli degil";
    }

    private static string ResolveLastErrorSummary(AdminActivationControlCenterViewModel activationControlCenter, AdminOperationsRuntimeHealthCenterViewModel runtimeCenter)
    {
        if (string.Equals(activationControlCenter.LastDecision.TypeLabel, "Block", StringComparison.OrdinalIgnoreCase))
        {
            return ResolveActivationBlockedSummary(activationControlCenter);
        }

        if (string.Equals(runtimeCenter.StatusTone, "healthy", StringComparison.OrdinalIgnoreCase))
        {
            return "Yok";
        }

        var failureSummary = runtimeCenter.LastFailureSummary ?? string.Empty;
        if (ContainsToken(failureSummary, "worker"))
        {
            return "Worker calismiyor";
        }

        if (ContainsToken(failureSummary, "clock") || ContainsToken(failureSummary, "drift"))
        {
            return "Saat senkronu hazir degil";
        }

        if (ContainsToken(failureSummary, "market") || ContainsToken(failureSummary, "continuity"))
        {
            return "Piyasa verisi guncel degil";
        }

        if (ContainsToken(failureSummary, "exchange"))
        {
            return "Exchange baglantisi hazir degil";
        }

        if (ContainsToken(failureSummary, "retry"))
        {
            return "Sistem tekrar deniyor";
        }

        return "Sistem hatasi var";
    }

    private static string ResolveLastStopSummary(GlobalExecutionSwitchSnapshot executionSnapshot, GlobalSystemStateSnapshot globalSystemStateSnapshot, AdminActivationControlCenterViewModel activationControlCenter)
    {
        if (globalSystemStateSnapshot.State == GlobalSystemStateKind.FullHalt)
        {
            return "Acil durdurma aktif";
        }

        if (globalSystemStateSnapshot.State == GlobalSystemStateKind.SoftHalt)
        {
            return "Soft halt aktif";
        }

        if (!executionSnapshot.IsTradeMasterArmed)
        {
            return "TradeMaster kapali";
        }

        if (activationControlCenter.IsCurrentlyActive)
        {
            return "Yok";
        }

        return string.Equals(activationControlCenter.LastDecision.TypeLabel, "Block", StringComparison.OrdinalIgnoreCase)
            ? ResolveActivationBlockedSummary(activationControlCenter)
            : "Sistem kapali";
    }

    private static string ResolveTradeMasterValue(AdminActivationControlCenterViewModel activationControlCenter)
    {
        var value = ResolveSwitchValue(activationControlCenter, "trade-master", "Disarmed");
        return string.Equals(value, "Armed", StringComparison.OrdinalIgnoreCase)
            ? "Acik"
            : string.Equals(value, "Active", StringComparison.OrdinalIgnoreCase)
                ? "Acik"
                : "Kapali";
    }

    private static string ResolveSwitchValue(AdminActivationControlCenterViewModel activationControlCenter, string key, string fallback)
    {
        return activationControlCenter.CriticalSwitches.FirstOrDefault(item => string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase))?.Value ?? fallback;
    }

    private static string ResolveSwitchTone(AdminActivationControlCenterViewModel activationControlCenter, string key, string fallback)
    {
        return activationControlCenter.CriticalSwitches.FirstOrDefault(item => string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase))?.Tone ?? fallback;
    }

    private static string ResolveSwitchSummary(AdminActivationControlCenterViewModel activationControlCenter, string key, string fallback)
    {
        return activationControlCenter.CriticalSwitches.FirstOrDefault(item => string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase))?.Summary ?? fallback;
    }

    private static string ResolveMonitoringStatusSummary(string monitoringStatus)
    {
        return monitoringStatus switch
        {
            "Aktif" => "Sistem su anda aktif.",
            "Durduruldu" => "Sistem su anda kapali.",
            "Acil durdurma aktif" => "Acil durdurma aktif.",
            _ => "Izleme ozeti eksik."
        };
    }

    private static int CountRunningBots(AdminBotOperationsPageSnapshot botOperationsSnapshot)
    {
        return botOperationsSnapshot.Bots.Count(bot =>
            string.Equals(bot.StatusTone, "healthy", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(bot.StatusLabel, "Aktif", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(bot.StatusLabel, "Running", StringComparison.OrdinalIgnoreCase));
    }

    private static string ResolveMonitoringDisplayStatus(string monitoringStatus)
    {
        return monitoringStatus switch
        {
            "Aktif" => "Sistem aktif",
            "Durduruldu" => "Sistem kapali",
            _ => monitoringStatus
        };
    }

    private static string ResolveLastErrorDisplay(string lastErrorSummary)
    {
        return string.IsNullOrWhiteSpace(lastErrorSummary) ||
               string.Equals(lastErrorSummary, "Yok", StringComparison.OrdinalIgnoreCase)
            ? "Son hata yok"
            : lastErrorSummary;
    }

    private static string ResolveLastStopDisplay(string lastStopSummary)
    {
        if (string.IsNullOrWhiteSpace(lastStopSummary) ||
            string.Equals(lastStopSummary, "Yok", StringComparison.OrdinalIgnoreCase))
        {
            return "Son durdurma nedeni yok";
        }

        return string.Equals(lastStopSummary, "Soft halt aktif", StringComparison.OrdinalIgnoreCase)
            ? "Sistem kapali"
            : lastStopSummary;
    }

    private static string ResolveLastOperationTimeLabel(AdminBotOperationsPageSnapshot botOperationsSnapshot, int runningBotCount)
    {
        return runningBotCount > 0
            ? FormatUtc(botOperationsSnapshot.LastRefreshedAtUtc)
            : "Son islem yok";
    }

    private static bool ContainsToken(string? value, string token)
    {
        return !string.IsNullOrWhiteSpace(value) && value.Contains(token, StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatUtc(DateTime utcDateTime)
    {
        return utcDateTime.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'");
    }
}




