using CoinBot.Application.Abstractions.Administration;
using CoinBot.Application.Abstractions.Execution;
using CoinBot.Application.Abstractions.Exchange;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Jobs;

namespace CoinBot.Web.ViewModels.Admin;

public sealed record AdminActivationControlCenterViewModel(
    bool IsCurrentlyActive,
    bool IsActivatable,
    string StatusLabel,
    string StatusTone,
    string Guidance,
    string CurrentModeLabel,
    string CurrentModeSummary,
    AdminActivationDecisionViewModel LastDecision,
    IReadOnlyCollection<AdminActivationSwitchViewModel> CriticalSwitches,
    IReadOnlyCollection<AdminActivationReadinessItemViewModel> ReadinessChecklist)
{
    public IReadOnlyCollection<AdminActivationReadinessItemViewModel> BlockingItems =>
        ReadinessChecklist.Where(item => !item.IsPassing).ToArray();
}

public sealed record AdminActivationDecisionViewModel(
    string TypeLabel,
    string Tone,
    string Code,
    string Summary,
    string Source,
    string EvaluatedAtUtcLabel,
    string StateSummary);

public sealed record AdminActivationSwitchViewModel(
    string Key,
    string Label,
    string Value,
    string Tone,
    string Summary);

public sealed record AdminActivationReadinessItemViewModel(
    string Key,
    string Label,
    string StatusLabel,
    string StatusCode,
    string Tone,
    string ReasonCode,
    string Summary,
    string SourceHint)
{
    public bool IsPassing => string.Equals(StatusCode, "pass", StringComparison.OrdinalIgnoreCase);
}

public static class AdminActivationControlCenterComposer
{
    public static AdminActivationControlCenterViewModel Compose(
        GlobalExecutionSwitchSnapshot executionSnapshot,
        GlobalSystemStateSnapshot globalSystemStateSnapshot,
        BinanceTimeSyncSnapshot clockDriftSnapshot,
        DegradedModeSnapshot driftGuardSnapshot,
        BotExecutionPilotOptions pilotOptions,
        string pilotOrderNotionalSummary,
        string pilotOrderNotionalTone,
        DateTime evaluatedAtUtc)
    {
        var readinessChecklist = new List<AdminActivationReadinessItemViewModel>
        {
            BuildConfigStateItem(executionSnapshot, globalSystemStateSnapshot),
            BuildPilotActivationItem(pilotOptions),
            BuildTradingModeItem(executionSnapshot),
            BuildGlobalSystemStateItem(globalSystemStateSnapshot),
            BuildServerTimeItem(clockDriftSnapshot),
            BuildReadinessGateItem(driftGuardSnapshot),
            BuildPilotNotionalItem(pilotOrderNotionalSummary, pilotOrderNotionalTone)
        };

        var blockingItems = readinessChecklist
            .Where(item => !item.IsPassing)
            .ToArray();
        var isActivatable = blockingItems.Length == 0;
        var isCurrentlyActive = isActivatable && executionSnapshot.IsTradeMasterArmed;
        var decision = BuildDecision(
            executionSnapshot,
            globalSystemStateSnapshot,
            blockingItems,
            evaluatedAtUtc,
            isCurrentlyActive,
            isActivatable);
        var criticalSwitches = BuildCriticalSwitches(executionSnapshot, globalSystemStateSnapshot, pilotOptions);
        var guidance = isCurrentlyActive
            ? "TradeMaster armed ve readiness checklist gecerli; sistem secili modda calismaya hazir."
            : isActivatable
                ? "Readiness checklist gecerli; aktivasyon komutu TradeMaster'i armed duruma getirebilir."
                : "Readiness checklist bloklu veya unknown; sistem aktive edilemez.";
        var statusLabel = isCurrentlyActive
            ? "Aktif"
            : isActivatable
                ? "Aktif edilebilir"
                : "Aktif edilemez";
        var statusTone = isCurrentlyActive
            ? "healthy"
            : isActivatable
                ? "warning"
                : "critical";
        var currentModeLabel = executionSnapshot.DemoModeEnabled ? "Demo" : "Live";
        var currentModeSummary = executionSnapshot.DemoModeEnabled
            ? "Demo execution yolu acik; aktivasyon live'a gecis yapmaz."
            : executionSnapshot.HasLiveModeApproval
                ? "Live execution icin approval reference dogrulanmis."
                : "Live mode secili ama approval reference dogrulanamadi.";

        return new AdminActivationControlCenterViewModel(
            isCurrentlyActive,
            isActivatable,
            statusLabel,
            statusTone,
            guidance,
            currentModeLabel,
            currentModeSummary,
            decision,
            criticalSwitches,
            readinessChecklist);
    }

    private static AdminActivationDecisionViewModel BuildDecision(
        GlobalExecutionSwitchSnapshot executionSnapshot,
        GlobalSystemStateSnapshot globalSystemStateSnapshot,
        IReadOnlyCollection<AdminActivationReadinessItemViewModel> blockingItems,
        DateTime evaluatedAtUtc,
        bool isCurrentlyActive,
        bool isActivatable)
    {
        var evaluationLabel = FormatUtc(evaluatedAtUtc);
        var stateSummary = string.Join(
            " | ",
            $"Mode={(executionSnapshot.DemoModeEnabled ? "Demo" : "Live")}",
            $"TradeMaster={(executionSnapshot.IsTradeMasterArmed ? "Armed" : "Disarmed")}",
            $"PilotActivation={(isCurrentlyActive || isActivatable ? "Ready" : "Blocked")}",
            $"GlobalState={globalSystemStateSnapshot.State}");

        if (blockingItems.Count > 0)
        {
            var primaryBlocker = blockingItems.First();

            return new AdminActivationDecisionViewModel(
                "Block",
                primaryBlocker.Tone,
                primaryBlocker.ReasonCode,
                primaryBlocker.Summary,
                primaryBlocker.SourceHint,
                evaluationLabel,
                stateSummary);
        }

        if (isCurrentlyActive)
        {
            return new AdminActivationDecisionViewModel(
                "Allow",
                "healthy",
                "AlreadyActive",
                "TradeMaster armed ve readiness checklist gecerli; execution yolu acik.",
                "ActivationControlCenter.Readiness",
                evaluationLabel,
                stateSummary);
        }

        return new AdminActivationDecisionViewModel(
            "Allow",
            "warning",
            "ActivationReady",
            "Readiness checklist gecerli; aktivasyon komutu sistemi armed duruma getirebilir.",
            "ActivationControlCenter.Readiness",
            evaluationLabel,
            stateSummary);
    }

    private static IReadOnlyCollection<AdminActivationSwitchViewModel> BuildCriticalSwitches(
        GlobalExecutionSwitchSnapshot executionSnapshot,
        GlobalSystemStateSnapshot globalSystemStateSnapshot,
        BotExecutionPilotOptions pilotOptions)
    {
        var killSwitchActive = !executionSnapshot.IsTradeMasterArmed;
        var softHaltActive = globalSystemStateSnapshot.State == GlobalSystemStateKind.SoftHalt;
        var emergencyStopActive = globalSystemStateSnapshot.State == GlobalSystemStateKind.FullHalt;

        return new[]
        {
            new AdminActivationSwitchViewModel(
                "trade-master",
                "TradeMaster",
                executionSnapshot.IsTradeMasterArmed ? "Armed" : "Disarmed",
                executionSnapshot.IsTradeMasterArmed ? "healthy" : "critical",
                "Master execution switch. Armed olmadiginda emir akisi fail-closed durur."),
            new AdminActivationSwitchViewModel(
                "pilot-activation",
                "PilotActivation",
                pilotOptions.PilotActivationEnabled ? "Enabled" : "Disabled",
                pilotOptions.PilotActivationEnabled ? "healthy" : "critical",
                "Development futures pilot submit yolu config bazli acilir veya kapanir."),
            new AdminActivationSwitchViewModel(
                "global-switch",
                "Global execution switch",
                executionSnapshot.IsPersisted ? "Persisted" : "Fail-closed",
                executionSnapshot.IsPersisted ? "healthy" : "critical",
                "Snapshot kalici olarak okunamiyorsa aktivasyon karari fail-closed bloklanir."),
            new AdminActivationSwitchViewModel(
                "trading-mode",
                "Trading mode",
                executionSnapshot.DemoModeEnabled ? "Demo" : "Live",
                executionSnapshot.DemoModeEnabled ? "info" : executionSnapshot.HasLiveModeApproval ? "warning" : "critical",
                executionSnapshot.DemoModeEnabled
                    ? "Demo mode acikken aktivasyon demo execution yolu icin gecerlidir."
                    : executionSnapshot.HasLiveModeApproval
                        ? "Live mode approval reference ile korunur."
                        : "Live mode approval reference eksik veya okunamadi."),
            new AdminActivationSwitchViewModel(
                "kill-switch",
                "Kill switch",
                killSwitchActive ? "Active" : "Clear",
                killSwitchActive ? "critical" : "healthy",
                "Bu yuzeyde kill switch gorevi TradeMaster disarm durumu ile temsil edilir."),
            new AdminActivationSwitchViewModel(
                "soft-halt",
                "Soft halt",
                softHaltActive ? "Active" : "Clear",
                softHaltActive ? "critical" : "healthy",
                "Soft halt aktifse yeni aktivasyon verilmez; once state Active'a donmelidir."),
            new AdminActivationSwitchViewModel(
                "emergency-stop",
                "Emergency stop",
                emergencyStopActive ? "Active" : "Clear",
                emergencyStopActive ? "critical" : "healthy",
                "Emergency stop aktifse tum aktivasyon kararları bloklanir.")
        };
    }

    private static AdminActivationReadinessItemViewModel BuildConfigStateItem(
        GlobalExecutionSwitchSnapshot executionSnapshot,
        GlobalSystemStateSnapshot globalSystemStateSnapshot)
    {
        if (executionSnapshot.IsPersisted && globalSystemStateSnapshot.IsPersisted)
        {
            return CreateReadinessItem(
                "config-state",
                "Config / state okunuyor",
                "pass",
                "ActivationStateReadable",
                "Global execution switch ve global system state snapshot'lari kalici olarak okunuyor.",
                "GlobalExecutionSwitchService + GlobalSystemStateService");
        }

        return CreateReadinessItem(
            "config-state",
            "Config / state okunuyor",
            "unknown",
            "ActivationStateUnavailable",
            "Global execution switch veya global system state snapshot'i eksik; aktivasyon fail-closed bloklandi.",
            "GlobalExecutionSwitchService + GlobalSystemStateService");
    }

    private static AdminActivationReadinessItemViewModel BuildPilotActivationItem(BotExecutionPilotOptions pilotOptions)
    {
        return pilotOptions.PilotActivationEnabled
            ? CreateReadinessItem(
                "pilot-activation",
                "Pilot activation hazir",
                "pass",
                "PilotActivationEnabled",
                "PilotActivationEnabled=true; submit yolu config seviyesinde acik.",
                "BotExecutionPilotOptions")
            : CreateReadinessItem(
                "pilot-activation",
                "Pilot activation hazir",
                "fail",
                "PilotActivationDisabled",
                "PilotActivationEnabled=false; sistem aktivasyonu submit yolu acmadan bloklanir.",
                "BotExecutionPilotOptions");
    }

    private static AdminActivationReadinessItemViewModel BuildTradingModeItem(GlobalExecutionSwitchSnapshot executionSnapshot)
    {
        if (executionSnapshot.DemoModeEnabled)
        {
            return CreateReadinessItem(
                "trading-mode",
                "Calisma modu gecerli",
                "pass",
                "DemoModeReady",
                "Global default Demo; aktivasyon demo execution yolu icin hazir.",
                "GlobalExecutionSwitchService");
        }

        return executionSnapshot.HasLiveModeApproval
            ? CreateReadinessItem(
                "trading-mode",
                "Calisma modu gecerli",
                "pass",
                "LiveModeApproved",
                "Global default Live ve explicit approval reference mevcut.",
                "GlobalExecutionSwitchService")
            : CreateReadinessItem(
                "trading-mode",
                "Calisma modu gecerli",
                "fail",
                "LiveModeApprovalMissing",
                "Live mode secili gorunuyor ancak approval reference bulunmadi; aktivasyon bloklandi.",
                "GlobalExecutionSwitchService");
    }

    private static AdminActivationReadinessItemViewModel BuildGlobalSystemStateItem(GlobalSystemStateSnapshot snapshot)
    {
        if (!snapshot.IsPersisted)
        {
            return CreateReadinessItem(
                "global-system-state",
                "Global system state uygun",
                "unknown",
                "GlobalSystemStateUnavailable",
                "Global system state snapshot'i eksik veya stale; aktivasyon fail-closed bloklandi.",
                "GlobalSystemStateService");
        }

        return snapshot.State switch
        {
            GlobalSystemStateKind.Active => CreateReadinessItem(
                "global-system-state",
                "Global system state uygun",
                "pass",
                "GlobalSystemActive",
                $"Global system state {snapshot.State}; execution path merkezi state guard tarafinda acik.",
                "GlobalSystemStateService"),
            GlobalSystemStateKind.SoftHalt => CreateReadinessItem(
                "global-system-state",
                "Global system state uygun",
                "fail",
                "GlobalSystemSoftHalt",
                "Global system state SoftHalt; yeni aktivasyon verilmez.",
                "GlobalSystemStateService"),
            GlobalSystemStateKind.FullHalt => CreateReadinessItem(
                "global-system-state",
                "Global system state uygun",
                "fail",
                "GlobalSystemFullHalt",
                "Global system state FullHalt; emergency stop aktif oldugu icin aktivasyon verilmez.",
                "GlobalSystemStateService"),
            GlobalSystemStateKind.Maintenance => CreateReadinessItem(
                "global-system-state",
                "Global system state uygun",
                "fail",
                "GlobalSystemMaintenance",
                "Global system state Maintenance; aktivasyon once state temizlenmeden verilemez.",
                "GlobalSystemStateService"),
            _ => CreateReadinessItem(
                "global-system-state",
                "Global system state uygun",
                "fail",
                "GlobalSystemDegraded",
                "Global system state Degraded; fail-closed guard nedeniyle aktivasyon verilmez.",
                "GlobalSystemStateService")
        };
    }

    private static AdminActivationReadinessItemViewModel BuildServerTimeItem(BinanceTimeSyncSnapshot snapshot)
    {
        if (snapshot.HasSynchronizedOffset &&
            string.IsNullOrWhiteSpace(snapshot.FailureReason))
        {
            return CreateReadinessItem(
                "server-time-sync",
                "Server-time sync hazir",
                "pass",
                "ServerTimeSynchronized",
                $"Server-time sync {snapshot.StatusCode}; son offset {snapshot.OffsetMilliseconds} ms.",
                "BinanceTimeSyncService");
        }

        return CreateReadinessItem(
            "server-time-sync",
            "Server-time sync hazir",
            string.IsNullOrWhiteSpace(snapshot.FailureReason) ? "unknown" : "fail",
            "ServerTimeSyncUnavailable",
            string.IsNullOrWhiteSpace(snapshot.FailureReason)
                ? "Server-time sync snapshot'i tam degil; activation gate fail-closed bloklandi."
                : $"Server-time sync saglikli degil: {snapshot.FailureReason}",
            "BinanceTimeSyncService");
    }

    private static AdminActivationReadinessItemViewModel BuildReadinessGateItem(DegradedModeSnapshot snapshot)
    {
        if (!snapshot.IsPersisted)
        {
            return CreateReadinessItem(
                "readiness-gate",
                "Readiness / health gate uygun",
                "unknown",
                "DataLatencyGuardUnavailable",
                "Data latency guard snapshot'i eksik; readiness unknown oldugu icin aktivasyon bloklandi.",
                "DataLatencyCircuitBreaker");
        }

        return snapshot.IsNormal
            ? CreateReadinessItem(
                "readiness-gate",
                "Readiness / health gate uygun",
                "pass",
                "ReadinessGateNormal",
                "Data latency guard Normal; signal ve execution flow bloklu degil.",
                "DataLatencyCircuitBreaker")
            : CreateReadinessItem(
                "readiness-gate",
                "Readiness / health gate uygun",
                "fail",
                snapshot.ReasonCode.ToString(),
                BuildReadinessGateSummary(snapshot),
                "DataLatencyCircuitBreaker");
    }

    private static AdminActivationReadinessItemViewModel BuildPilotNotionalItem(
        string pilotOrderNotionalSummary,
        string pilotOrderNotionalTone)
    {
        if (string.Equals(pilotOrderNotionalTone, "healthy", StringComparison.OrdinalIgnoreCase))
        {
            return CreateReadinessItem(
                "pilot-notional-cap",
                "Pilot hard cap gecerli",
                "pass",
                "PilotOrderNotionalBound",
                $"Pilot order notional ust siniri {pilotOrderNotionalSummary} olarak okunuyor.",
                "BotExecutionPilotOptions");
        }

        var missing = pilotOrderNotionalSummary.StartsWith("Missing", StringComparison.OrdinalIgnoreCase);

        return CreateReadinessItem(
            "pilot-notional-cap",
            "Pilot hard cap gecerli",
            "fail",
            missing ? "PilotOrderNotionalMissing" : "PilotOrderNotionalInvalid",
            missing
                ? "Pilot hard cap config'i eksik; submit oncesi notional guard fail-closed bloklanir."
                : $"Pilot hard cap config'i gecersiz: {pilotOrderNotionalSummary}.",
            "BotExecutionPilotOptions");
    }

    private static string BuildReadinessGateSummary(DegradedModeSnapshot snapshot)
    {
        return snapshot.ReasonCode switch
        {
            DegradedModeReasonCode.ClockDriftExceeded => "Clock drift guard threshold ustunde; aktivasyon verilmez.",
            DegradedModeReasonCode.MarketDataLatencyBreached => "Market-data latency breached; readiness fail durumunda.",
            DegradedModeReasonCode.MarketDataLatencyCritical => "Market-data latency critical; execution flow bloklu.",
            DegradedModeReasonCode.MarketDataUnavailable => "Market-data heartbeat snapshot'i yok veya stale; readiness unknown kabul edilmez.",
            DegradedModeReasonCode.CandleDataGapDetected => "Continuity gap tespit edildi; aktivasyon once veri butunlugu toparlanmadan verilmez.",
            DegradedModeReasonCode.CandleDataDuplicateDetected => "Duplicate candle tespit edildi; readiness fail-closed bloklanir.",
            DegradedModeReasonCode.CandleDataOutOfOrderDetected => "Out-of-order candle tespit edildi; readiness fail-closed bloklanir.",
            _ => "Readiness gate normal disi; aktivasyon once guard temizlenmeden verilemez."
        };
    }

    private static AdminActivationReadinessItemViewModel CreateReadinessItem(
        string key,
        string label,
        string statusCode,
        string reasonCode,
        string summary,
        string sourceHint)
    {
        var normalizedStatusCode = statusCode.Trim().ToLowerInvariant();
        var (statusLabel, tone) = normalizedStatusCode switch
        {
            "pass" => ("Pass", "healthy"),
            "fail" => ("Fail", "critical"),
            _ => ("Unknown", "warning")
        };

        return new AdminActivationReadinessItemViewModel(
            key,
            label,
            statusLabel,
            normalizedStatusCode,
            tone,
            reasonCode,
            summary,
            sourceHint);
    }

    private static string FormatUtc(DateTime utcTimestamp)
    {
        var normalizedUtcTimestamp = utcTimestamp.Kind == DateTimeKind.Utc
            ? utcTimestamp
            : DateTime.SpecifyKind(utcTimestamp, DateTimeKind.Utc);

        return $"{normalizedUtcTimestamp:yyyy-MM-dd HH:mm:ss} UTC";
    }
}
