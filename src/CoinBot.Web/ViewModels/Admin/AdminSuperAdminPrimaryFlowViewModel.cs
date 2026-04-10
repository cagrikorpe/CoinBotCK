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
    IReadOnlyCollection<AdminSuperAdminFlowItemViewModel> Items);

public sealed record AdminSuperAdminFlowItemViewModel(
    string Key,
    string Label,
    string Value,
    string Tone,
    string Summary,
    string? Href = null);

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
            new[]
            {
                new AdminSuperAdminFlowItemViewModel(
                    "access",
                    "Erisim",
                    "Blocked",
                    "critical",
                    $"Fail-closed. Refreshed {FormatUtc(evaluatedAtUtc)}")
            });

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
        DateTime evaluatedAtUtc)
    {
        var workerSignal = runtimeCenter.Signals.FirstOrDefault(item => string.Equals(item.Key, "worker-health", StringComparison.OrdinalIgnoreCase));
        var exchangeReady = HasReadyExchange(exchangeCenter, activationControlCenter.CurrentModeLabel);
        var workerReady = workerSignal is not null && string.Equals(workerSignal.Tone, "healthy", StringComparison.OrdinalIgnoreCase);
        var emergencyActive = IsEmergencyActive(activationControlCenter, globalSystemStateSnapshot);
        var setupReady = exchangeReady && workerReady && activationControlCenter.IsActivatable;
        var activationStatus = ResolveActivationStatus(activationControlCenter, emergencyActive);
        var monitoringStatus = ResolveMonitoringStatus(executionSnapshot, emergencyActive, workerReady, exchangeReady);
        var runningBotCount = CountRunningBots(botOperationsSnapshot);
        var setupMessage = BuildSetupMessage(activationControlCenter, exchangeReady, workerReady);
        var activationMessage = BuildActivationMessage(activationControlCenter, exchangeReady, workerReady, emergencyActive);
        var monitoringMessage = BuildMonitoringMessage(executionSnapshot, activationControlCenter, runtimeCenter, emergencyActive, exchangeReady, workerReady);
        var lastErrorSummary = ResolveLastErrorSummary(activationControlCenter, runtimeCenter);
        var lastStopSummary = ResolveLastStopSummary(executionSnapshot, globalSystemStateSnapshot, activationControlCenter);

        return new AdminSuperAdminPrimaryFlowViewModel(
            new AdminSuperAdminFlowSectionViewModel(
                "setup",
                "Sistem Kurulumu",
                setupReady ? "Ready" : "Blocked",
                setupReady ? "healthy" : "critical",
                setupReady
                    ? "Ortam secimi, exchange baglantisi, worker ve temel hazirlik kapilari temiz gorunuyor."
                    : "Kurulum eksigi var; kaydetmeden once tek satir blocker mesajini giderin.",
                setupMessage,
                new[]
                {
                    new AdminSuperAdminFlowItemViewModel("environment", "Ortam", activationControlCenter.CurrentModeLabel, activationControlCenter.CurrentModeLabel.Equals("Live", StringComparison.OrdinalIgnoreCase) ? "warning" : "info", activationControlCenter.CurrentModeSummary),
                    new AdminSuperAdminFlowItemViewModel("exchange", "Exchange hesabi", exchangeReady ? "Ready" : "Blocked", exchangeReady ? "healthy" : "critical", exchangeReady ? "Secili ortam icin validated exchange hesabi gorunuyor." : ResolveExchangeBlockedSummary(activationControlCenter.CurrentModeLabel)),
                    new AdminSuperAdminFlowItemViewModel("worker", "Worker", workerReady ? "Ready" : "Blocked", workerReady ? "healthy" : "critical", workerReady ? "Worker heartbeat gorunuyor." : "Worker calismiyor veya heartbeat gorunmuyor."),
                    new AdminSuperAdminFlowItemViewModel("system-ready", "Sistem hazirligi", activationControlCenter.IsActivatable ? "Ready" : "Blocked", activationControlCenter.IsActivatable ? "healthy" : "critical", activationControlCenter.IsActivatable ? "Temel aktivasyon checklist'i temiz." : "Sistem aktivasyona hazir degil."),
                    new AdminSuperAdminFlowItemViewModel("checked-at", "Son kontrol", FormatUtc(evaluatedAtUtc), "info", "Read-model ozetinin son degerlendirme zamani")
                }),
            new AdminSuperAdminFlowSectionViewModel(
                "activation",
                "Sistemi Aktiflestir",
                activationStatus,
                activationStatus switch
                {
                    "Active" => "healthy",
                    "Ready" => "warning",
                    "EmergencyStopActive" => "critical",
                    _ => "critical"
                },
                activationStatus switch
                {
                    "Active" => "Sistem armed durumda. Kapatma ve emergency aksiyonlari guarded yuzeylerden uygulanir.",
                    "Ready" => "Aktivasyon verilebilir; siradaki adim guarded aktivasyon komutudur.",
                    "EmergencyStopActive" => "Emergency stop aktifken aktivasyon verilmez.",
                    _ => "Aktivasyon once eksikleri kapatip tekrar degerlendirme ister."
                },
                activationMessage,
                new[]
                {
                    new AdminSuperAdminFlowItemViewModel("environment", "Ortam ozeti", activationControlCenter.CurrentModeLabel, activationControlCenter.CurrentModeLabel.Equals("Live", StringComparison.OrdinalIgnoreCase) ? "warning" : "info", activationControlCenter.CurrentModeSummary),
                    new AdminSuperAdminFlowItemViewModel("exchange", "Exchange hazirligi", exchangeReady ? "Ready" : "Blocked", exchangeReady ? "healthy" : "critical", exchangeReady ? "Exchange baglantisi hazir." : ResolveExchangeBlockedSummary(activationControlCenter.CurrentModeLabel)),
                    new AdminSuperAdminFlowItemViewModel("worker", "Worker hazirligi", workerReady ? "Ready" : "Blocked", workerReady ? "healthy" : "critical", workerReady ? "Worker heartbeat gorunuyor." : "Worker calismiyor."),
                    new AdminSuperAdminFlowItemViewModel("trade-master", "TradeMaster", ResolveSwitchValue(activationControlCenter, "trade-master", "Disarmed"), ResolveSwitchTone(activationControlCenter, "trade-master", "critical"), ResolveSwitchSummary(activationControlCenter, "trade-master", "TradeMaster kapali.")),
                    new AdminSuperAdminFlowItemViewModel("system-active", "Sistem aktif mi", activationControlCenter.IsCurrentlyActive ? "Active" : "Stopped", activationControlCenter.IsCurrentlyActive ? "healthy" : "warning", activationControlCenter.LastDecision.Summary),
                    new AdminSuperAdminFlowItemViewModel("decision", "Son karar", activationControlCenter.LastDecision.Code, activationControlCenter.LastDecision.Tone, activationControlCenter.LastDecision.EvaluatedAtUtcLabel)
                }),
            new AdminSuperAdminFlowSectionViewModel(
                "monitoring",
                "Sistemi Izle",
                monitoringStatus,
                monitoringStatus switch
                {
                    "Active" => "healthy",
                    "Stopped" => "warning",
                    "EmergencyStopActive" => "critical",
                    _ => "critical"
                },
                monitoringStatus switch
                {
                    "Active" => "Sistem calisiyor; son hata ve durdurma nedeni bu ekranda kisa ozetle izlenir.",
                    "Stopped" => "Sistem kapali; tekrar acmadan once son hata ve durdurma nedenini kontrol edin.",
                    "EmergencyStopActive" => "Emergency stop aktif; rollout ve aktivasyon yeniden verilmez.",
                    _ => "Izleme sinyalleri eksik; sistem guvenli sekilde bloke gorunur."
                },
                monitoringMessage,
                new[]
                {
                    new AdminSuperAdminFlowItemViewModel("status", "Sistem durumu", monitoringStatus, monitoringStatus switch { "Active" => "healthy", "Stopped" => "warning", _ => "critical" }, activationControlCenter.Guidance),
                    new AdminSuperAdminFlowItemViewModel("environment", "Ortam", activationControlCenter.CurrentModeLabel, activationControlCenter.CurrentModeLabel.Equals("Live", StringComparison.OrdinalIgnoreCase) ? "warning" : "info", activationControlCenter.CurrentModeSummary),
                    new AdminSuperAdminFlowItemViewModel("running-bots", "Calisan bot sayisi", runningBotCount.ToString(), runningBotCount > 0 ? "healthy" : "warning", $"Toplam bot gorunurlugu {botOperationsSnapshot.Bots.Count}"),
                    new AdminSuperAdminFlowItemViewModel("last-operation", "Son islem zamani", activationControlCenter.LastDecision.EvaluatedAtUtcLabel, "info", "Son aktivasyon/readiness karari"),
                    new AdminSuperAdminFlowItemViewModel("last-error", "Son hata", string.IsNullOrWhiteSpace(lastErrorSummary) ? "Yok" : lastErrorSummary, string.IsNullOrWhiteSpace(lastErrorSummary) || string.Equals(lastErrorSummary, "Yok", StringComparison.OrdinalIgnoreCase) ? "healthy" : "warning", runtimeCenter.LastFailureSummary),
                    new AdminSuperAdminFlowItemViewModel("last-stop", "Son durdurma nedeni", lastStopSummary, lastStopSummary.Equals("Yok", StringComparison.OrdinalIgnoreCase) ? "healthy" : "warning", globalSystemStateSnapshot.ReasonCode)
                }),
            new AdminSuperAdminFlowSectionViewModel(
                "advanced",
                "Gelismis",
                "Ready",
                "info",
                "Teknik denetim, audit, trace ve rollout detaylari ana akistan ayrik tutulur.",
                "Detaylar sadece gerektiginde acilir.",
                new[]
                {
                    new AdminSuperAdminFlowItemViewModel("global-settings", "Global Ayarlar", "Ac", "info", "Tum teknik ayarlar ve guarded config formlari", "/admin/Settings"),
                    new AdminSuperAdminFlowItemViewModel("audit", "Audit", "Ac", "warning", "Karar zinciri, admin aksiyonlari ve before/after", "/admin/Audit"),
                    new AdminSuperAdminFlowItemViewModel("incidents", "Incidents", "Ac", "warning", "Incident detaylari ve timeline", "/admin/Incidents"),
                    new AdminSuperAdminFlowItemViewModel("health", "Health detaylari", "Ac", "info", "Runtime health ve dependency detaylari", "/admin/SystemHealth"),
                    new AdminSuperAdminFlowItemViewModel("history", "Config / State History", "Ac", "info", "Degisim gecmisi ve rollout izleri", "/admin/ConfigHistory")
                }));
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
            return "EmergencyStopActive";
        }

        if (activationControlCenter.IsCurrentlyActive)
        {
            return "Active";
        }

        return activationControlCenter.IsActivatable
            ? "Ready"
            : "Blocked";
    }

    private static string ResolveMonitoringStatus(GlobalExecutionSwitchSnapshot executionSnapshot, bool emergencyActive, bool workerReady, bool exchangeReady)
    {
        if (emergencyActive)
        {
            return "EmergencyStopActive";
        }

        if (executionSnapshot.IsTradeMasterArmed)
        {
            return "Active";
        }

        return workerReady || exchangeReady
            ? "Stopped"
            : "Blocked";
    }

    private static int CountRunningBots(AdminBotOperationsPageSnapshot botOperationsSnapshot)
    {
        return botOperationsSnapshot.Bots.Count(bot =>
            string.Equals(bot.StatusTone, "healthy", StringComparison.OrdinalIgnoreCase) &&
            !ContainsToken(bot.StatusLabel, "disabled") &&
            !ContainsToken(bot.StatusLabel, "blocked") &&
            !ContainsToken(bot.StatusLabel, "stopped"));
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
            ? "Ready"
            : "Sistem hazir degil";
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
            ? "Sistem aktif"
            : "Ready";
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
            ? "Sistem calisiyor"
            : (string.Equals(runtimeCenter.StatusTone, "critical", StringComparison.OrdinalIgnoreCase) ? "Izleme sinyali bloklu" : "Sistem kapali");
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
            "ClockDriftUnavailable" or "ClockDriftExceeded" => "Saat senkronu hazir degil",
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

        return string.Equals(runtimeCenter.StatusTone, "healthy", StringComparison.OrdinalIgnoreCase)
            ? "Yok"
            : runtimeCenter.LastFailureSummary;
    }

    private static string ResolveLastStopSummary(GlobalExecutionSwitchSnapshot executionSnapshot, GlobalSystemStateSnapshot globalSystemStateSnapshot, AdminActivationControlCenterViewModel activationControlCenter)
    {
        if (globalSystemStateSnapshot.State == GlobalSystemStateKind.FullHalt)
        {
            return "Emergency stop aktif";
        }

        if (globalSystemStateSnapshot.State == GlobalSystemStateKind.SoftHalt)
        {
            return "Soft halt aktif";
        }

        if (!executionSnapshot.IsTradeMasterArmed)
        {
            return "TradeMaster kapali";
        }

        return activationControlCenter.IsCurrentlyActive ? "Yok" : activationControlCenter.LastDecision.Summary;
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

    private static bool ContainsToken(string? value, string token)
    {
        return !string.IsNullOrWhiteSpace(value) && value.Contains(token, StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatUtc(DateTime utcDateTime)
    {
        return utcDateTime.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'");
    }
}
