using System.Globalization;
using CoinBot.Application.Abstractions.Administration;
using CoinBot.Application.Abstractions.Execution;
using CoinBot.Application.Abstractions.Exchange;
using CoinBot.Application.Abstractions.Monitoring;
using CoinBot.Application.Abstractions.Policy;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Jobs;

namespace CoinBot.Web.ViewModels.Admin;

public sealed record AdminOperationsCenterViewModel(
    bool IsAccessible,
    string AccessTitle,
    string AccessSummary,
    string RefreshedAtUtcLabel,
    IReadOnlyCollection<AdminOperationsSummaryCardViewModel> SummaryCards,
    AdminOperationsRuntimeHealthCenterViewModel RuntimeHealthCenter,
    AdminOperationsUserBotGovernanceCenterViewModel UserBotGovernanceCenter,
    AdminOperationsExchangeGovernanceCenterViewModel ExchangeGovernanceCenter,
    AdminOperationsPolicyGovernanceCenterViewModel PolicyGovernanceCenter);

public sealed record AdminOperationsSummaryCardViewModel(
    string Label,
    string Value,
    string Tone,
    string Summary);

public sealed record AdminOperationsRuntimeHealthCenterViewModel(
    string StatusLabel,
    string StatusTone,
    string Summary,
    string LastFailureSummary,
    string RetrySummary,
    IReadOnlyCollection<AdminOperationsSignalViewModel> Signals);

public sealed record AdminOperationsSignalViewModel(
    string Key,
    string Label,
    string Value,
    string Tone,
    string Summary,
    string Code);

public sealed record AdminOperationsUserBotGovernanceCenterViewModel(
    string Summary,
    IReadOnlyCollection<AdminOperationsSummaryCardViewModel> SummaryCards,
    IReadOnlyCollection<AdminOperationsGovernanceRowViewModel> ProblemUsers,
    IReadOnlyCollection<AdminOperationsGovernanceRowViewModel> ProblemBots);

public sealed record AdminOperationsGovernanceRowViewModel(
    string Key,
    string Label,
    string OwnerLabel,
    string StatusLabel,
    string Tone,
    string Summary,
    string Meta,
    IReadOnlyCollection<string> Flags);

public sealed record AdminOperationsExchangeGovernanceCenterViewModel(
    string Summary,
    IReadOnlyCollection<AdminOperationsSummaryCardViewModel> SummaryCards,
    IReadOnlyCollection<AdminOperationsCredentialRowViewModel> Accounts);

public sealed record AdminOperationsCredentialRowViewModel(
    string ExchangeAccountId,
    string OwnerLabel,
    string DisplayLabel,
    string ValidationLabel,
    string Tone,
    string EnvironmentLabel,
    string CapabilityLabel,
    string AccessLabel,
    string FingerprintLabel,
    string Summary,
    string LastValidationLabel,
    string LastFailureSummary);

public sealed record AdminOperationsPolicyGovernanceCenterViewModel(
    string Summary,
    IReadOnlyCollection<AdminOperationsSummaryCardViewModel> SummaryCards,
    IReadOnlyCollection<AdminOperationsPolicyItemViewModel> GlobalDefaults,
    IReadOnlyCollection<AdminOperationsPolicyItemViewModel> PilotScope,
    IReadOnlyCollection<AdminOperationsPolicyItemViewModel> Retention,
    IReadOnlyCollection<AdminOperationsPolicyItemViewModel> EmergencyPolicy);

public sealed record AdminOperationsPolicyItemViewModel(
    string Label,
    string Value,
    string Tone,
    string Summary);


public sealed record AdminRolloutClosureCenterViewModel(
    bool IsAccessible,
    string StatusLabel,
    string StatusTone,
    string Summary,
    string RefreshedAtUtcLabel,
    IReadOnlyCollection<AdminRolloutStageViewModel> Stages,
    IReadOnlyCollection<AdminRolloutStatusItemViewModel> MandatoryGates,
    IReadOnlyCollection<AdminRolloutStatusItemViewModel> GoLiveChecklist,
    IReadOnlyCollection<AdminRolloutStatusItemViewModel> BlockingReasons,
    IReadOnlyCollection<AdminRolloutLinkedItemViewModel> RelatedLinks,
    string RollbackSummary,
    string EmergencySummary);

public sealed record AdminRolloutStageViewModel(
    string Key,
    string Label,
    string StatusLabel,
    string Tone,
    string Summary,
    string BlockingReasons,
    string EvaluatedAtUtcLabel,
    string ChangedBy);

public sealed record AdminRolloutStatusItemViewModel(
    string Key,
    string Label,
    string StatusLabel,
    string Tone,
    string Summary,
    string ReasonCode,
    string SourceLabel);

public sealed record AdminRolloutLinkedItemViewModel(
    string Label,
    string Summary,
    string Tone,
    string? Href,
    string Meta);

public sealed record AdminRolloutEvidenceInput(
    string Key,
    string Label,
    bool IsPassing,
    string ReasonCode,
    string Summary,
    string SourceLabel,
    DateTime? EvaluatedAtUtc);
public static class AdminOperationsCenterComposer
{
    public static AdminOperationsCenterViewModel CreateAccessDenied(DateTime evaluatedAtUtc)
    {
        return new AdminOperationsCenterViewModel(
            IsAccessible: false,
            AccessTitle: "Super Admin gerekli",
            AccessSummary: "Bu operasyon merkezi yalnizca global activation, exchange governance ve policy override yetkisine sahip Super Admin rolu icin acilir.",
            RefreshedAtUtcLabel: FormatUtc(evaluatedAtUtc),
            SummaryCards:
            [
                new AdminOperationsSummaryCardViewModel(
                    "Erisim",
                    "Blocked",
                    "critical",
                    "Super Admin rolu olmadan operasyon read-model ve kritik aksiyonlar gosterilmez.")
            ],
            RuntimeHealthCenter: new AdminOperationsRuntimeHealthCenterViewModel(
                "Blocked",
                "critical",
                "Runtime merkezi fail-closed olarak gizlendi.",
                "Super Admin role missing.",
                "n/a",
                Array.Empty<AdminOperationsSignalViewModel>()),
            UserBotGovernanceCenter: new AdminOperationsUserBotGovernanceCenterViewModel(
                "Kullanici ve bot governance merkezi Super Admin yetkisi olmadan gosterilmez.",
                Array.Empty<AdminOperationsSummaryCardViewModel>(),
                Array.Empty<AdminOperationsGovernanceRowViewModel>(),
                Array.Empty<AdminOperationsGovernanceRowViewModel>()),
            ExchangeGovernanceCenter: new AdminOperationsExchangeGovernanceCenterViewModel(
                "Credential inventory fail-closed olarak gizlendi.",
                Array.Empty<AdminOperationsSummaryCardViewModel>(),
                Array.Empty<AdminOperationsCredentialRowViewModel>()),
            PolicyGovernanceCenter: new AdminOperationsPolicyGovernanceCenterViewModel(
                "Global policy ve limit governance only Super Admin.",
                Array.Empty<AdminOperationsSummaryCardViewModel>(),
                Array.Empty<AdminOperationsPolicyItemViewModel>(),
                Array.Empty<AdminOperationsPolicyItemViewModel>(),
                Array.Empty<AdminOperationsPolicyItemViewModel>(),
                Array.Empty<AdminOperationsPolicyItemViewModel>()));
    }

    public static AdminOperationsCenterViewModel Compose(
        AdminActivationControlCenterViewModel activationControlCenter,
        MonitoringDashboardSnapshot monitoringDashboard,
        BinanceTimeSyncSnapshot clockDriftSnapshot,
        DegradedModeSnapshot driftGuardSnapshot,
        AdminUsersPageSnapshot usersSnapshot,
        AdminBotOperationsPageSnapshot botOperationsSnapshot,
        IReadOnlyCollection<ApiCredentialAdminSummary> credentialSummaries,
        GlobalPolicySnapshot globalPolicySnapshot,
        BotExecutionPilotOptions pilotOptions,
        LogCenterRetentionSnapshot? retentionSnapshot,
        GlobalSystemStateSnapshot globalSystemStateSnapshot,
        DateTime evaluatedAtUtc)
    {
        var runtimeCenter = BuildRuntimeHealthCenter(
            monitoringDashboard,
            clockDriftSnapshot,
            driftGuardSnapshot);
        var userBotCenter = BuildUserBotGovernanceCenter(usersSnapshot, botOperationsSnapshot);
        var exchangeCenter = BuildExchangeGovernanceCenter(credentialSummaries);
        var policyCenter = BuildPolicyGovernanceCenter(
            activationControlCenter,
            globalPolicySnapshot,
            pilotOptions,
            retentionSnapshot,
            globalSystemStateSnapshot);
        var summaryCards = BuildSummaryCards(
            activationControlCenter,
            runtimeCenter,
            userBotCenter,
            exchangeCenter,
            globalSystemStateSnapshot,
            evaluatedAtUtc);

        return new AdminOperationsCenterViewModel(
            IsAccessible: true,
            AccessTitle: "Super Admin Operasyon Merkezi",
            AccessSummary: "Runtime, user/bot, credential ve policy yuzeyleri tek read-model omurgasi uzerinden gorunur. Unknown durumlar healthy sayilmaz.",
            RefreshedAtUtcLabel: FormatUtc(evaluatedAtUtc),
            SummaryCards: summaryCards,
            RuntimeHealthCenter: runtimeCenter,
            UserBotGovernanceCenter: userBotCenter,
            ExchangeGovernanceCenter: exchangeCenter,
            PolicyGovernanceCenter: policyCenter);
    }

    private static IReadOnlyCollection<AdminOperationsSummaryCardViewModel> BuildSummaryCards(
        AdminActivationControlCenterViewModel activationControlCenter,
        AdminOperationsRuntimeHealthCenterViewModel runtimeCenter,
        AdminOperationsUserBotGovernanceCenterViewModel userBotCenter,
        AdminOperationsExchangeGovernanceCenterViewModel exchangeCenter,
        GlobalSystemStateSnapshot globalSystemStateSnapshot,
        DateTime evaluatedAtUtc)
    {
        var problemBotCard = userBotCenter.SummaryCards.FirstOrDefault(item =>
            string.Equals(item.Label, "Problemli bot", StringComparison.OrdinalIgnoreCase));
        var credentialIssueCard = exchangeCenter.SummaryCards.FirstOrDefault(item =>
            string.Equals(item.Label, "Validation issue", StringComparison.OrdinalIgnoreCase));

        return
        [
            new AdminOperationsSummaryCardViewModel(
                "Sistem durumu",
                activationControlCenter.StatusLabel,
                activationControlCenter.StatusTone,
                activationControlCenter.Guidance),
            new AdminOperationsSummaryCardViewModel(
                "CanActivate",
                activationControlCenter.IsActivatable ? "true" : "false",
                activationControlCenter.IsActivatable ? "healthy" : "critical",
                $"{activationControlCenter.LastDecision.Code} - {activationControlCenter.LastDecision.Summary}"),
            new AdminOperationsSummaryCardViewModel(
                "Calisma modu",
                activationControlCenter.CurrentModeLabel,
                activationControlCenter.CurrentModeLabel.Equals("Live", StringComparison.OrdinalIgnoreCase) ? "warning" : "info",
                activationControlCenter.CurrentModeSummary),
            new AdminOperationsSummaryCardViewModel(
                "Runtime sagligi",
                runtimeCenter.StatusLabel,
                runtimeCenter.StatusTone,
                runtimeCenter.Summary),
            new AdminOperationsSummaryCardViewModel(
                "Problemli bot",
                problemBotCard?.Value ?? "0",
                problemBotCard?.Tone ?? "info",
                problemBotCard?.Summary ?? "Problemli bot segmenti uretilemedi."),
            new AdminOperationsSummaryCardViewModel(
                "Credential issue",
                credentialIssueCard?.Value ?? "0",
                credentialIssueCard?.Tone ?? "info",
                credentialIssueCard?.Summary ?? "Credential issue read-modeli bos."),
            new AdminOperationsSummaryCardViewModel(
                "Son state degisikligi",
                globalSystemStateSnapshot.State.ToString(),
                globalSystemStateSnapshot.IsExecutionBlocked ? "critical" : "healthy",
                $"Updated {FormatUtc(globalSystemStateSnapshot.UpdatedAtUtc)} by {(globalSystemStateSnapshot.UpdatedByUserId ?? "n/a")}"),
            new AdminOperationsSummaryCardViewModel(
                "Dashboard refresh",
                FormatUtc(evaluatedAtUtc),
                "info",
                "Monitoring + settings snapshotlari tek operatif ozet uzerinden gorunur.")
        ];
    }

    private static AdminOperationsRuntimeHealthCenterViewModel BuildRuntimeHealthCenter(
        MonitoringDashboardSnapshot monitoringDashboard,
        BinanceTimeSyncSnapshot clockDriftSnapshot,
        DegradedModeSnapshot driftGuardSnapshot)
    {
        var workerSignal = BuildWorkerSignal(monitoringDashboard);
        var exchangeSyncSignal = BuildExchangeSyncSignal(monitoringDashboard);
        var clockDriftSignal = BuildClockDriftSignal(clockDriftSnapshot);
        var marketFreshnessSignal = BuildMarketFreshnessSignal(monitoringDashboard, driftGuardSnapshot);
        var degradedModeSignal = BuildDegradedModeSignal(driftGuardSnapshot);
        var retrySignal = BuildRetrySignal(monitoringDashboard);

        var signals = new[]
        {
            workerSignal,
            exchangeSyncSignal,
            clockDriftSignal,
            marketFreshnessSignal,
            degradedModeSignal,
            retrySignal
        };

        var criticalCount = signals.Count(item => string.Equals(item.Tone, "critical", StringComparison.OrdinalIgnoreCase));
        var warningCount = signals.Count(item => string.Equals(item.Tone, "warning", StringComparison.OrdinalIgnoreCase) || string.Equals(item.Tone, "degraded", StringComparison.OrdinalIgnoreCase));
        var statusTone = criticalCount > 0
            ? "critical"
            : warningCount > 0
                ? "warning"
                : "healthy";
        var statusLabel = criticalCount > 0
            ? "Action required"
            : warningCount > 0
                ? "Review"
                : "Healthy";
        var summary = criticalCount > 0
            ? $"{criticalCount} kritik runtime sinyali var; aktivasyon ve trade yolu tekrar kontrol edilmeli."
            : warningCount > 0
                ? $"{warningCount} runtime sinyali review gerektiriyor."
                : "Runtime health, drift ve freshness sinyalleri operasyon icin uygun gorunuyor.";
        var lastFailureSummary = signals
            .Where(item => string.Equals(item.Tone, "critical", StringComparison.OrdinalIgnoreCase) || string.Equals(item.Tone, "warning", StringComparison.OrdinalIgnoreCase) || string.Equals(item.Tone, "degraded", StringComparison.OrdinalIgnoreCase))
            .Select(item => $"{item.Code}: {item.Summary}")
            .FirstOrDefault()
            ?? "Aktif failure signal yok.";
        var retrySummary = retrySignal.Summary;

        return new AdminOperationsRuntimeHealthCenterViewModel(
            statusLabel,
            statusTone,
            summary,
            lastFailureSummary,
            retrySummary,
            signals);
    }

    private static AdminOperationsSignalViewModel BuildWorkerSignal(MonitoringDashboardSnapshot monitoringDashboard)
    {
        var workers = monitoringDashboard.WorkerHeartbeats;

        if (workers.Count == 0)
        {
            return new AdminOperationsSignalViewModel(
                "worker-health",
                "Worker health",
                "Unknown",
                "critical",
                "Worker heartbeat read-modeli bos; worker ayakta olsa bile snapshot yoksa fail-closed review gerekir.",
                "WorkerHeartbeatUnavailable");
        }

        var staleCount = workers.Count(worker => worker.FreshnessTier == MonitoringFreshnessTier.Stale);
        var criticalCount = workers.Count(worker => worker.HealthState == MonitoringHealthState.Critical);
        var latestHeartbeat = workers.Max(worker => worker.LastHeartbeatAtUtc);

        if (criticalCount > 0 || staleCount > 0)
        {
            return new AdminOperationsSignalViewModel(
                "worker-health",
                "Worker health",
                $"{workers.Count - staleCount}/{workers.Count} healthy-ish",
                criticalCount > 0 ? "critical" : "warning",
                $"{staleCount} stale worker, {criticalCount} critical worker. Last heartbeat {FormatUtc(latestHeartbeat)}.",
                criticalCount > 0 ? "WorkerCritical" : "WorkerStale");
        }

        return new AdminOperationsSignalViewModel(
            "worker-health",
            "Worker health",
            $"{workers.Count} active",
            "healthy",
            $"Tum worker heartbeat'leri fresh. Last heartbeat {FormatUtc(latestHeartbeat)}.",
            "WorkerHealthy");
    }

    private static AdminOperationsSignalViewModel BuildExchangeSyncSignal(MonitoringDashboardSnapshot monitoringDashboard)
    {
        var privateStreamWorker = monitoringDashboard.WorkerHeartbeats.FirstOrDefault(worker =>
            string.Equals(worker.WorkerKey, "exchange-private-stream", StringComparison.OrdinalIgnoreCase));
        var dependencyHealth = monitoringDashboard.HealthSnapshots.FirstOrDefault(snapshot =>
            string.Equals(snapshot.SnapshotKey, "dependency-health-monitor", StringComparison.OrdinalIgnoreCase));

        if (privateStreamWorker is null && dependencyHealth is null)
        {
            return new AdminOperationsSignalViewModel(
                "exchange-sync",
                "Exchange sync",
                "Unknown",
                "critical",
                "Private stream veya dependency snapshot'i bulunamadi; exchange sync gorunurlugu fail-closed bloklandi.",
                "ExchangeSyncUnknown");
        }

        if (privateStreamWorker is not null)
        {
            var tone = privateStreamWorker.HealthState == MonitoringHealthState.Healthy &&
                       privateStreamWorker.FreshnessTier != MonitoringFreshnessTier.Stale
                ? "healthy"
                : privateStreamWorker.FreshnessTier == MonitoringFreshnessTier.Stale ||
                  privateStreamWorker.HealthState == MonitoringHealthState.Critical
                    ? "critical"
                    : "warning";
            var code = tone == "healthy"
                ? "ExchangeSyncHealthy"
                : privateStreamWorker.FreshnessTier == MonitoringFreshnessTier.Stale
                    ? "ExchangeSyncStale"
                    : "ExchangeSyncReview";

            return new AdminOperationsSignalViewModel(
                "exchange-sync",
                "Exchange sync",
                privateStreamWorker.CircuitBreakerState.ToString(),
                tone,
                $"{privateStreamWorker.WorkerName} heartbeat {FormatUtc(privateStreamWorker.LastHeartbeatAtUtc)} · Error={(privateStreamWorker.LastErrorCode ?? "none")}",
                code);
        }

        var dependencyTone = dependencyHealth!.HealthState switch
        {
            MonitoringHealthState.Healthy => "healthy",
            MonitoringHealthState.Warning => "warning",
            MonitoringHealthState.Degraded => "warning",
            MonitoringHealthState.Critical => "critical",
            _ => "critical"
        };

        return new AdminOperationsSignalViewModel(
            "exchange-sync",
            "Exchange sync",
            dependencyHealth.CircuitBreakerState.ToString(),
            dependencyTone,
            dependencyHealth.Detail ?? "Dependency health snapshot exchange sync review istiyor.",
            dependencyTone == "healthy" ? "ExchangeSyncHealthy" : "ExchangeSyncDependencyReview");
    }

    private static AdminOperationsSignalViewModel BuildClockDriftSignal(BinanceTimeSyncSnapshot clockDriftSnapshot)
    {
        var driftLabel = clockDriftSnapshot.ClockDriftMilliseconds?.ToString() ?? "n/a";
        var synchronized = string.Equals(clockDriftSnapshot.StatusCode, "Synchronized", StringComparison.OrdinalIgnoreCase);
        var tone = synchronized
            ? clockDriftSnapshot.ClockDriftMilliseconds is > 1000 ? "warning" : "healthy"
            : "critical";
        var code = synchronized
            ? clockDriftSnapshot.ClockDriftMilliseconds is > 1000 ? "ClockDriftReview" : "ClockDriftHealthy"
            : string.IsNullOrWhiteSpace(clockDriftSnapshot.FailureReason) ? "ClockDriftUnavailable" : "ClockDriftFailed";

        return new AdminOperationsSignalViewModel(
            "clock-drift",
            "Clock drift",
            synchronized ? $"{driftLabel} ms" : clockDriftSnapshot.StatusCode,
            tone,
            synchronized
                ? $"Last sync {FormatUtc(clockDriftSnapshot.LastSynchronizedAtUtc)}"
                : (clockDriftSnapshot.FailureReason ?? "Server time sync unavailable."),
            code);
    }

    private static AdminOperationsSignalViewModel BuildMarketFreshnessSignal(
        MonitoringDashboardSnapshot monitoringDashboard,
        DegradedModeSnapshot driftGuardSnapshot)
    {
        var marketDataCache = monitoringDashboard.MarketDataCache;
        var staleReads = marketDataCache.StaleHitCount + marketDataCache.ProviderUnavailableCount + marketDataCache.InvalidPayloadCount + marketDataCache.DeserializeFailedCount;
        var symbolCount = marketDataCache.SymbolFreshness.Count;

        if (!driftGuardSnapshot.IsPersisted && symbolCount == 0)
        {
            return new AdminOperationsSignalViewModel(
                "market-freshness",
                "Market freshness",
                "Unknown",
                "critical",
                "Drift guard ve market data cache snapshot'i eksik; freshness durumu fail-closed okunur.",
                "MarketFreshnessUnknown");
        }

        if (!driftGuardSnapshot.IsNormal || staleReads > 0)
        {
            return new AdminOperationsSignalViewModel(
                "market-freshness",
                "Market freshness",
                driftGuardSnapshot.ReasonCode.ToString(),
                driftGuardSnapshot.IsNormal ? "warning" : "critical",
                $"State={driftGuardSnapshot.StateCode}; StaleReads={staleReads}; LatestSymbol={(driftGuardSnapshot.LatestSymbol ?? "n/a")} / {(driftGuardSnapshot.LatestTimeframe ?? "n/a")}",
                !driftGuardSnapshot.IsNormal ? "MarketFreshnessBreached" : "MarketCacheReview");
        }

        return new AdminOperationsSignalViewModel(
            "market-freshness",
            "Market freshness",
            "Fresh",
            "healthy",
            $"Cache reads={marketDataCache.HitCount}; Symbol projections={symbolCount}; Drift guard normal.",
            "MarketFreshnessHealthy");
    }

    private static AdminOperationsSignalViewModel BuildDegradedModeSignal(DegradedModeSnapshot driftGuardSnapshot)
    {
        if (!driftGuardSnapshot.IsPersisted)
        {
            return new AdminOperationsSignalViewModel(
                "degraded-mode",
                "Degraded mode",
                "Unknown",
                "critical",
                "Degraded mode snapshot'i persisted degil; execution gate sagligi dogrulanamadi.",
                "DegradedModeUnknown");
        }

        if (!driftGuardSnapshot.IsNormal)
        {
            return new AdminOperationsSignalViewModel(
                "degraded-mode",
                "Degraded mode",
                driftGuardSnapshot.StateCode.ToString(),
                "critical",
                $"{driftGuardSnapshot.ReasonCode} · SignalBlocked={driftGuardSnapshot.SignalFlowBlocked} · ExecutionBlocked={driftGuardSnapshot.ExecutionFlowBlocked}",
                "DegradedModeActive");
        }

        return new AdminOperationsSignalViewModel(
            "degraded-mode",
            "Degraded mode",
            "Normal",
            "healthy",
            $"Last changed {FormatUtc(driftGuardSnapshot.LastStateChangedAtUtc)}",
            "DegradedModeClear");
    }

    private static AdminOperationsSignalViewModel BuildRetrySignal(MonitoringDashboardSnapshot monitoringDashboard)
    {
        var retryingWorker = monitoringDashboard.WorkerHeartbeats
            .OrderByDescending(worker => worker.ConsecutiveFailureCount)
            .ThenByDescending(worker => worker.LastUpdatedAtUtc)
            .FirstOrDefault(worker =>
                worker.ConsecutiveFailureCount > 0 ||
                !string.IsNullOrWhiteSpace(worker.LastErrorCode) ||
                !string.IsNullOrWhiteSpace(worker.LastErrorMessage));

        if (retryingWorker is null)
        {
            return new AdminOperationsSignalViewModel(
                "retry-state",
                "Retry / son hata",
                "Clear",
                "healthy",
                "Aktif retry spirali veya son hata kaydi yok.",
                "RetryClear");
        }

        var tone = retryingWorker.ConsecutiveFailureCount >= 3 || retryingWorker.HealthState == MonitoringHealthState.Critical
            ? "critical"
            : "warning";

        return new AdminOperationsSignalViewModel(
            "retry-state",
            "Retry / son hata",
            $"{retryingWorker.ConsecutiveFailureCount}x retry",
            tone,
            $"{retryingWorker.WorkerName} · {(retryingWorker.LastErrorCode ?? "NoCode")} · {(retryingWorker.LastErrorMessage ?? retryingWorker.Detail ?? "No detail")}",
            tone == "critical" ? "RetryCritical" : "RetryReview");
    }

    private static AdminOperationsUserBotGovernanceCenterViewModel BuildUserBotGovernanceCenter(
        AdminUsersPageSnapshot usersSnapshot,
        AdminBotOperationsPageSnapshot botOperationsSnapshot)
    {
        var users = usersSnapshot.Users ?? Array.Empty<AdminUserListItemSnapshot>();
        var bots = botOperationsSnapshot.Bots ?? Array.Empty<AdminBotOperationSnapshot>();

        var problemUsers = users
            .Where(IsProblemUser)
            .OrderByDescending(ComputeUserSeverity)
            .ThenBy(user => user.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .Select(BuildProblemUserRow)
            .ToArray();
        var problemBots = bots
            .Where(IsProblemBot)
            .OrderByDescending(ComputeBotSeverity)
            .ThenBy(bot => bot.Name, StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .Select(BuildProblemBotRow)
            .ToArray();

        var summary = problemUsers.Length == 0 && problemBots.Length == 0
            ? "Kritik user veya bot sinyali gorunmuyor. Ayrintili filtre ve aksiyonlar icin Users / Bot Operations sayfalarina gecilebilir."
            : $"{problemUsers.Length} kullanici ve {problemBots.Length} bot satiri review gerektiriyor.";

        return new AdminOperationsUserBotGovernanceCenterViewModel(
            summary,
            [
                new AdminOperationsSummaryCardViewModel(
                    "Toplam kullanici",
                    users.Count.ToString(),
                    "info",
                    $"Last refresh {FormatUtc(usersSnapshot.LastRefreshedAtUtc)}"),
                new AdminOperationsSummaryCardViewModel(
                    "Problemli kullanici",
                    problemUsers.Length.ToString(),
                    problemUsers.Length > 0 ? "warning" : "healthy",
                    "Status, MFA, trading mode ve last activity sinyalleri ile segmentlenir."),
                new AdminOperationsSummaryCardViewModel(
                    "Toplam bot",
                    bots.Count.ToString(),
                    "info",
                    $"Last refresh {FormatUtc(botOperationsSnapshot.LastRefreshedAtUtc)}"),
                new AdminOperationsSummaryCardViewModel(
                    "Problemli bot",
                    problemBots.Length.ToString(),
                    problemBots.Length > 0 ? "critical" : "healthy",
                    "Stale, cooldown, blocked ve repeated error sinyalleri ustte toplanir.")
            ],
            problemUsers,
            problemBots);
    }

    private static bool IsProblemUser(AdminUserListItemSnapshot user)
    {
        return !string.Equals(user.StatusTone, "healthy", StringComparison.OrdinalIgnoreCase) ||
               !string.Equals(user.MfaTone, "healthy", StringComparison.OrdinalIgnoreCase) ||
               !string.Equals(user.TradingModeTone, "healthy", StringComparison.OrdinalIgnoreCase) ||
               !string.Equals(user.LastActivityTone, "healthy", StringComparison.OrdinalIgnoreCase);
    }

    private static int ComputeUserSeverity(AdminUserListItemSnapshot user)
    {
        var severity = 0;
        severity += ToneSeverity(user.StatusTone) * 4;
        severity += ToneSeverity(user.MfaTone) * 2;
        severity += ToneSeverity(user.TradingModeTone) * 2;
        severity += ToneSeverity(user.LastActivityTone);
        return severity;
    }

    private static AdminOperationsGovernanceRowViewModel BuildProblemUserRow(AdminUserListItemSnapshot user)
    {
        var flags = new List<string>();
        if (!string.Equals(user.StatusTone, "healthy", StringComparison.OrdinalIgnoreCase))
        {
            flags.Add("UserStatusReview");
        }

        if (!string.Equals(user.MfaTone, "healthy", StringComparison.OrdinalIgnoreCase))
        {
            flags.Add("MfaReview");
        }

        if (!string.Equals(user.TradingModeTone, "healthy", StringComparison.OrdinalIgnoreCase))
        {
            flags.Add("TradingModeReview");
        }

        if (!string.Equals(user.LastActivityTone, "healthy", StringComparison.OrdinalIgnoreCase))
        {
            flags.Add("LastActivityReview");
        }

        var tone = HighestTone(user.StatusTone, user.MfaTone, user.TradingModeTone, user.LastActivityTone);
        var summary = $"Role={user.RoleSummary}; TradingMode={user.TradingModeLabel}; Bots={user.BotCount}; Exchanges={user.ExchangeCount}";

        return new AdminOperationsGovernanceRowViewModel(
            user.UserId,
            user.DisplayName,
            user.UserName,
            user.StatusLabel,
            tone,
            summary,
            $"Last activity {user.LastActivityLabel}",
            flags);
    }

    private static bool IsProblemBot(AdminBotOperationSnapshot bot)
    {
        return !string.Equals(bot.StatusTone, "healthy", StringComparison.OrdinalIgnoreCase) ||
               !string.Equals(bot.RiskTone, "healthy", StringComparison.OrdinalIgnoreCase) ||
               !string.IsNullOrWhiteSpace(bot.LastError);
    }

    private static int ComputeBotSeverity(AdminBotOperationSnapshot bot)
    {
        var severity = 0;
        severity += ToneSeverity(bot.StatusTone) * 4;
        severity += ToneSeverity(bot.RiskTone) * 2;
        severity += string.IsNullOrWhiteSpace(bot.LastError) ? 0 : 4;
        severity += bot.OpenOrderCount > 0 ? 1 : 0;
        severity += bot.OpenPositionCount > 0 ? 1 : 0;
        return severity;
    }

    private static AdminOperationsGovernanceRowViewModel BuildProblemBotRow(AdminBotOperationSnapshot bot)
    {
        var flags = new List<string>();
        var combinedText = $"{bot.StatusLabel} {bot.LastAction} {bot.LastError}";

        if (ContainsToken(combinedText, "stale") || ContainsToken(combinedText, "heartbeat"))
        {
            flags.Add("BotStale");
        }

        if (ContainsToken(combinedText, "cooldown"))
        {
            flags.Add("BotCooldown");
        }

        if (ContainsToken(combinedText, "block") || ContainsToken(combinedText, "degraded"))
        {
            flags.Add("BotBlocked");
        }

        if (ContainsToken(combinedText, "stuck") || (bot.OpenOrderCount > 0 && !string.IsNullOrWhiteSpace(bot.LastError)))
        {
            flags.Add("BotStuck");
        }

        if (flags.Count == 0 && !string.IsNullOrWhiteSpace(bot.LastError))
        {
            flags.Add("BotFailing");
        }

        if (!string.Equals(bot.StatusTone, "healthy", StringComparison.OrdinalIgnoreCase))
        {
            flags.Add("BotStatusReview");
        }

        var tone = HighestTone(bot.StatusTone, bot.RiskTone, string.IsNullOrWhiteSpace(bot.LastError) ? "healthy" : "warning");
        var summary = $"{bot.StatusLabel} · {bot.ModeLabel} · Risk={bot.RiskLabel} · Strategy={bot.StrategyKey}";
        var meta = $"Owner={bot.OwnerDisplayName} · LastAction={bot.LastAction} · Orders={bot.OpenOrderCount} · Positions={bot.OpenPositionCount}";

        return new AdminOperationsGovernanceRowViewModel(
            bot.BotId,
            bot.Name,
            bot.OwnerDisplayName,
            bot.StatusLabel,
            tone,
            summary,
            meta,
            flags);
    }

    private static AdminOperationsExchangeGovernanceCenterViewModel BuildExchangeGovernanceCenter(
        IReadOnlyCollection<ApiCredentialAdminSummary> credentialSummaries)
    {
        var rows = credentialSummaries
            .Select(BuildCredentialRow)
            .OrderByDescending(row => ToneSeverity(row.Tone))
            .ThenBy(row => row.OwnerLabel, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.DisplayLabel, StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .ToArray();
        var validationIssueCount = rows.Count(row => !string.Equals(row.ValidationLabel, "Valid", StringComparison.OrdinalIgnoreCase));
        var writableCount = rows.Count(row => row.AccessLabel.Contains("Writable", StringComparison.OrdinalIgnoreCase));
        var summary = rows.Length == 0
            ? "Credential inventory bos. Unknown durum operatif olarak healthy sayilmaz; user/exchange onboarding audit edilmeli."
            : $"{rows.Length} credential satiri gosteriliyor. {validationIssueCount} validation issue mevcut, {writableCount} satir write-capable.";

        return new AdminOperationsExchangeGovernanceCenterViewModel(
            summary,
            [
                new AdminOperationsSummaryCardViewModel(
                    "Toplam hesap",
                    rows.Length.ToString(),
                    rows.Length > 0 ? "info" : "warning",
                    "Masked credential inventory"),
                new AdminOperationsSummaryCardViewModel(
                    "Validation issue",
                    validationIssueCount.ToString(),
                    validationIssueCount > 0 ? "critical" : "healthy",
                    "Invalid, missing, stale veya review gerektiren credential satirlari"),
                new AdminOperationsSummaryCardViewModel(
                    "Writable",
                    writableCount.ToString(),
                    writableCount > 0 ? "warning" : "info",
                    "Trade-capable credential sayisi"),
                new AdminOperationsSummaryCardViewModel(
                    "Readonly",
                    rows.Count(row => row.AccessLabel.Contains("Read-only", StringComparison.OrdinalIgnoreCase)).ToString(),
                    "info",
                    "Readonly credential'lar order dispatch icin kullanilmaz.")
            ],
            rows);
    }

    private static AdminOperationsCredentialRowViewModel BuildCredentialRow(ApiCredentialAdminSummary summary)
    {
        var parsedSummary = ParsePermissionSummary(summary.PermissionSummary);
        var environmentLabel = ResolveEnvironmentLabel(parsedSummary);
        var capabilityLabel = ResolveCapabilityLabel(parsedSummary);
        var accessLabel = ResolveAccessLabel(summary, parsedSummary);
        var tone = ResolveCredentialTone(summary.ValidationStatus, summary.LastFailureReason, environmentLabel);
        var lastValidationLabel = FormatUtc(summary.LastValidationAtUtc);
        var failureSummary = string.IsNullOrWhiteSpace(summary.LastFailureReason)
            ? "No failure detail"
            : summary.LastFailureReason!;
        var summaryText = $"{environmentLabel} · {capabilityLabel} · {accessLabel}";

        return new AdminOperationsCredentialRowViewModel(
            summary.ExchangeAccountId.ToString("N"),
            summary.OwnerUserId,
            $"{summary.ExchangeName} / {summary.DisplayName}",
            summary.ValidationStatus,
            tone,
            environmentLabel,
            capabilityLabel,
            accessLabel,
            summary.MaskedFingerprint ?? "missing",
            summaryText,
            lastValidationLabel,
            failureSummary);
    }

    private static AdminOperationsPolicyGovernanceCenterViewModel BuildPolicyGovernanceCenter(
        AdminActivationControlCenterViewModel activationControlCenter,
        GlobalPolicySnapshot globalPolicySnapshot,
        BotExecutionPilotOptions pilotOptions,
        LogCenterRetentionSnapshot? retentionSnapshot,
        GlobalSystemStateSnapshot globalSystemStateSnapshot)
    {
        var policy = globalPolicySnapshot.Policy;
        var guard = policy.ExecutionGuardPolicy;
        var restrictions = policy.SymbolRestrictions ?? Array.Empty<SymbolRestriction>();

        var globalDefaults =
            new[]
            {
                new AdminOperationsPolicyItemViewModel(
                    "Policy version",
                    $"v{globalPolicySnapshot.CurrentVersion}",
                    globalPolicySnapshot.IsPersisted ? "healthy" : "warning",
                    $"Source={(globalPolicySnapshot.IsPersisted ? "Persisted" : "Default fallback")} · Last change {(globalPolicySnapshot.LastChangeSummary ?? "n/a")}"),
                new AdminOperationsPolicyItemViewModel(
                    "Max order notional",
                    guard.MaxOrderNotional?.ToString() ?? "unbounded",
                    guard.MaxOrderNotional.HasValue ? "warning" : "info",
                    "Global execution guard effective value"),
                new AdminOperationsPolicyItemViewModel(
                    "Max position notional",
                    guard.MaxPositionNotional?.ToString() ?? "unbounded",
                    guard.MaxPositionNotional.HasValue ? "warning" : "info",
                    "Position cap effective value"),
                new AdminOperationsPolicyItemViewModel(
                    "Max daily trades",
                    guard.MaxDailyTrades?.ToString() ?? "unbounded",
                    guard.MaxDailyTrades.HasValue ? "warning" : "info",
                    "Trade frequency guard"),
                new AdminOperationsPolicyItemViewModel(
                    "Live approval mode",
                    policy.AutonomyPolicy.RequireManualApprovalForLive ? "Manual approval" : "Inline approval",
                    policy.AutonomyPolicy.RequireManualApprovalForLive ? "warning" : "info",
                    $"Autonomy mode {policy.AutonomyPolicy.Mode}")
            };

        var pilotScope =
            new[]
            {
                new AdminOperationsPolicyItemViewModel(
                    "Pilot activation",
                    pilotOptions.PilotActivationEnabled ? "Enabled" : "Disabled",
                    pilotOptions.PilotActivationEnabled ? "healthy" : "critical",
                    "Submit yolu config seviyesinde acilir veya kapanir."),
                new AdminOperationsPolicyItemViewModel(
                    "Pilot notional",
                    pilotOptions.MaxPilotOrderNotional ?? "missing",
                    pilotOptions.HasConfiguredMaxPilotOrderNotional() ? "warning" : "critical",
                    $"Default symbol {pilotOptions.DefaultSymbol} · leverage {pilotOptions.DefaultLeverage}"),
                new AdminOperationsPolicyItemViewModel(
                    "Pilot scope",
                    $"Users={pilotOptions.AllowedUserIds.Length} / Bots={pilotOptions.AllowedBotIds.Length}",
                    pilotOptions.AllowedUserIds.Length == 0 && pilotOptions.AllowedBotIds.Length == 0 ? "warning" : "info",
                    $"Symbols={pilotOptions.AllowedSymbols.Length} · Mode={pilotOptions.SignalEvaluationMode}"),
                new AdminOperationsPolicyItemViewModel(
                    "Cooldown",
                    $"{pilotOptions.PerBotCooldownSeconds}s / {pilotOptions.PerSymbolCooldownSeconds}s",
                    "info",
                    "Per-bot ve per-symbol throttle")
            };

        var retention = retentionSnapshot is null
            ? new[]
            {
                new AdminOperationsPolicyItemViewModel(
                    "Retention",
                    "Unknown",
                    "critical",
                    "Log retention snapshot'i okunamadi; unknown healthy sayilmaz.")
            }
            : new[]
            {
                new AdminOperationsPolicyItemViewModel(
                    "Audit retention",
                    $"{retentionSnapshot.AdminAuditLogRetentionDays} d",
                    "info",
                    $"Last run {FormatUtc(retentionSnapshot.LastRunAtUtc)}"),
                new AdminOperationsPolicyItemViewModel(
                    "Decision / execution",
                    $"{retentionSnapshot.DecisionTraceRetentionDays} d / {retentionSnapshot.ExecutionTraceRetentionDays} d",
                    "info",
                    $"Batch size {retentionSnapshot.BatchSize}"),
                new AdminOperationsPolicyItemViewModel(
                    "Incident / approval",
                    $"{retentionSnapshot.IncidentRetentionDays} d / {retentionSnapshot.ApprovalRetentionDays} d",
                    "info",
                    retentionSnapshot.LastRunSummary ?? "Retention run summary n/a")
            };

        var criticalSwitches = activationControlCenter.CriticalSwitches;
        var emergencyStop = criticalSwitches.FirstOrDefault(item => item.Key == "emergency-stop");
        var softHalt = criticalSwitches.FirstOrDefault(item => item.Key == "soft-halt");

        var emergencyPolicy = new[]
        {
            new AdminOperationsPolicyItemViewModel(
                "Global system state",
                globalSystemStateSnapshot.State.ToString(),
                globalSystemStateSnapshot.IsExecutionBlocked ? "critical" : "healthy",
                $"{globalSystemStateSnapshot.ReasonCode} · ManualOverride={globalSystemStateSnapshot.IsManualOverride}"),
            new AdminOperationsPolicyItemViewModel(
                "Emergency stop",
                emergencyStop?.Value ?? "Unknown",
                emergencyStop?.Tone ?? "critical",
                emergencyStop?.Summary ?? "Emergency stop state unavailable."),
            new AdminOperationsPolicyItemViewModel(
                "Soft halt",
                softHalt?.Value ?? "Unknown",
                softHalt?.Tone ?? "critical",
                softHalt?.Summary ?? "Soft halt state unavailable."),
            new AdminOperationsPolicyItemViewModel(
                "Activation decision",
                activationControlCenter.LastDecision.Code,
                activationControlCenter.LastDecision.Tone,
                $"{activationControlCenter.LastDecision.TypeLabel} · {activationControlCenter.LastDecision.Summary}")
        };

        return new AdminOperationsPolicyGovernanceCenterViewModel(
            $"Policy version v{globalPolicySnapshot.CurrentVersion}, {restrictions.Count} symbol restriction ve {(pilotOptions.PilotActivationEnabled ? "active" : "blocked")} pilot scope tek merkezde okunur.",
            [
                new AdminOperationsSummaryCardViewModel(
                    "Policy version",
                    $"v{globalPolicySnapshot.CurrentVersion}",
                    globalPolicySnapshot.IsPersisted ? "healthy" : "warning",
                    "Global defaults"),
                new AdminOperationsSummaryCardViewModel(
                    "Symbol restriction",
                    restrictions.Count.ToString(),
                    restrictions.Count > 0 ? "warning" : "info",
                    "Execution guard symbol kapsami"),
                new AdminOperationsSummaryCardViewModel(
                    "Pilot scope",
                    pilotOptions.PilotActivationEnabled ? "Enabled" : "Blocked",
                    pilotOptions.PilotActivationEnabled ? "healthy" : "critical",
                    $"Users={pilotOptions.AllowedUserIds.Length}, Bots={pilotOptions.AllowedBotIds.Length}"),
                new AdminOperationsSummaryCardViewModel(
                    "Emergency policy",
                    globalSystemStateSnapshot.State.ToString(),
                    globalSystemStateSnapshot.IsExecutionBlocked ? "critical" : "healthy",
                    activationControlCenter.LastDecision.Code)
            ],
            globalDefaults,
            pilotScope,
            retention,
            emergencyPolicy);
    }

    private static Dictionary<string, string> ParsePermissionSummary(string? permissionSummary)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(permissionSummary))
        {
            return result;
        }

        var segments = permissionSummary
            .Split(new[] { ';', ',', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var segment in segments)
        {
            var separatorIndex = segment.IndexOf('=');

            if (separatorIndex <= 0 || separatorIndex >= segment.Length - 1)
            {
                continue;
            }

            var key = segment[..separatorIndex].Trim();
            var value = segment[(separatorIndex + 1)..].Trim();

            if (key.Length == 0 || value.Length == 0)
            {
                continue;
            }

            result[key] = value;
        }

        return result;
    }

    private static string ResolveEnvironmentLabel(IReadOnlyDictionary<string, string> parsedSummary)
    {
        if (TryGetParsedValue(parsedSummary, out var environment, "Env", "Environment"))
        {
            return environment;
        }

        return "Unknown";
    }

    private static string ResolveCapabilityLabel(IReadOnlyDictionary<string, string> parsedSummary)
    {
        var capabilities = new List<string>();

        if (IsTruthy(parsedSummary, "Spot"))
        {
            capabilities.Add("Spot");
        }

        if (IsTruthy(parsedSummary, "Futures"))
        {
            capabilities.Add("Futures");
        }

        return capabilities.Count == 0 ? "Unknown capability" : string.Join(" + ", capabilities);
    }

    private static string ResolveAccessLabel(ApiCredentialAdminSummary summary, IReadOnlyDictionary<string, string> parsedSummary)
    {
        var tradeCapable = IsTruthy(parsedSummary, "Trade");
        var accessMode = summary.IsReadOnly || !tradeCapable ? "Read-only" : "Writable";
        return tradeCapable ? $"{accessMode} trade" : accessMode;
    }

    private static string ResolveCredentialTone(string validationStatus, string? lastFailureReason, string environmentLabel)
    {
        if (validationStatus.Equals("Valid", StringComparison.OrdinalIgnoreCase))
        {
            return environmentLabel.Equals("Unknown", StringComparison.OrdinalIgnoreCase) ? "warning" : "healthy";
        }

        if (validationStatus.Equals("Missing", StringComparison.OrdinalIgnoreCase) ||
            validationStatus.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
        {
            return "warning";
        }

        return string.IsNullOrWhiteSpace(lastFailureReason) ? "warning" : "critical";
    }

    private static bool TryGetParsedValue(IReadOnlyDictionary<string, string> parsedSummary, out string value, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (parsedSummary.TryGetValue(key, out value!))
            {
                return true;
            }
        }

        value = string.Empty;
        return false;
    }

    private static bool IsTruthy(IReadOnlyDictionary<string, string> parsedSummary, params string[] keys)
    {
        return TryGetParsedValue(parsedSummary, out var value, keys) &&
               (value.Equals("Y", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("Yes", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("True", StringComparison.OrdinalIgnoreCase));
    }

    private static bool ContainsToken(string? text, string token)
    {
        return !string.IsNullOrWhiteSpace(text) &&
               text.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static int ToneSeverity(string tone)
    {
        return tone.ToLowerInvariant() switch
        {
            "critical" => 4,
            "degraded" => 3,
            "warning" => 2,
            "info" => 1,
            _ => 0
        };
    }

    private static string HighestTone(params string[] tones)
    {
        return tones.OrderByDescending(ToneSeverity).FirstOrDefault() ?? "info";
    }


    public static AdminRolloutClosureCenterViewModel CreateAccessDeniedRolloutClosureCenter(DateTime evaluatedAtUtc)
    {
        var blockedItem = new AdminRolloutStatusItemViewModel(
            "super-admin-access",
            "Super Admin erisimi",
            "Blocked",
            "critical",
            "Rollout closure merkezi yalnizca Super Admin rolu ile acilir.",
            "SuperAdminRequired",
            "AdminPortal.Overview");

        return new AdminRolloutClosureCenterViewModel(
            IsAccessible: false,
            StatusLabel: "Blocked",
            StatusTone: "critical",
            Summary: "Super Admin rolu olmadan rollout closure merkezi fail-closed kapanir.",
            RefreshedAtUtcLabel: FormatUtc(evaluatedAtUtc),
            Stages:
            [
                CreateRolloutStage("stage-demo", "Demo/Testnet", "Blocked", "critical", "Super Admin gerekli.", "SuperAdminRequired", evaluatedAtUtc, "Unavailable"),
                CreateRolloutStage("stage-single-pilot", "Tek user + tek bot + tek symbol pilot", "Blocked", "critical", "Super Admin gerekli.", "SuperAdminRequired", evaluatedAtUtc, "Unavailable"),
                CreateRolloutStage("stage-live-pilot", "Sinirli canli pilot", "Blocked", "critical", "Super Admin gerekli.", "SuperAdminRequired", evaluatedAtUtc, "Unavailable"),
                CreateRolloutStage("stage-gradual", "Kademeli genisleme", "Blocked", "critical", "Super Admin gerekli.", "SuperAdminRequired", evaluatedAtUtc, "Unavailable"),
                CreateRolloutStage("stage-general", "Genel aktivasyon", "Blocked", "critical", "Super Admin gerekli.", "SuperAdminRequired", evaluatedAtUtc, "Unavailable")
            ],
            MandatoryGates: [blockedItem],
            GoLiveChecklist: Array.Empty<AdminRolloutStatusItemViewModel>(),
            BlockingReasons: [blockedItem],
            RelatedLinks:
            [
                new AdminRolloutLinkedItemViewModel(
                    "Audit / trace",
                    "Rollout karari icin Super Admin ile tekrar deneyin.",
                    "critical",
                    "/admin/Audit",
                    "Access denied")
            ],
            RollbackSummary: "Rollback ve emergency aksiyonlari yalnizca Super Admin rolu ile gorunur.",
            EmergencySummary: "Kill switch, soft halt ve emergency stop yuzeyleri bu rolde gizlenir.");
    }

    public static AdminRolloutClosureCenterViewModel BuildRolloutClosureCenter(
        AdminActivationControlCenterViewModel activationControlCenter,
        GlobalExecutionSwitchSnapshot executionSnapshot,
        GlobalSystemStateSnapshot globalSystemStateSnapshot,
        DegradedModeSnapshot driftGuardSnapshot,
        MonitoringDashboardSnapshot monitoringDashboard,
        IReadOnlyCollection<ApiCredentialAdminSummary> credentialSummaries,
        GlobalPolicySnapshot globalPolicySnapshot,
        BotExecutionPilotOptions pilotOptions,
        LogCenterPageSnapshot? logCenterSnapshot,
        LogCenterRetentionSnapshot? retentionSnapshot,
        IReadOnlyCollection<AdminRolloutEvidenceInput> evidenceInputs,
        DateTime evaluatedAtUtc)
    {
        var mandatoryGates = BuildRolloutMandatoryGates(
            activationControlCenter,
            executionSnapshot,
            driftGuardSnapshot,
            monitoringDashboard,
            credentialSummaries,
            evidenceInputs);
        var goLiveChecklist = BuildGoLiveChecklist(
            activationControlCenter,
            executionSnapshot,
            globalSystemStateSnapshot,
            credentialSummaries,
            globalPolicySnapshot,
            pilotOptions,
            logCenterSnapshot,
            retentionSnapshot);
        var blockingReasons = BuildRolloutBlockingReasons(mandatoryGates, goLiveChecklist);
        var stages = BuildRolloutStages(
            executionSnapshot,
            pilotOptions,
            credentialSummaries,
            globalSystemStateSnapshot,
            globalPolicySnapshot,
            blockingReasons,
            logCenterSnapshot,
            evaluatedAtUtc);
        var relatedLinks = BuildRolloutLinks(
            activationControlCenter,
            executionSnapshot,
            globalSystemStateSnapshot,
            logCenterSnapshot,
            evaluatedAtUtc);

        var hasBlockers = blockingReasons.Count > 0;
        var finalStageAllowed = stages.Any(item => string.Equals(item.Key, "stage-general", StringComparison.OrdinalIgnoreCase) &&
                                                   (string.Equals(item.StatusLabel, "Allowed", StringComparison.OrdinalIgnoreCase) ||
                                                    string.Equals(item.StatusLabel, "Current", StringComparison.OrdinalIgnoreCase)));
        var livePilotAllowed = stages.Any(item => string.Equals(item.Key, "stage-live-pilot", StringComparison.OrdinalIgnoreCase) &&
                                                  (string.Equals(item.StatusLabel, "Allowed", StringComparison.OrdinalIgnoreCase) ||
                                                   string.Equals(item.StatusLabel, "Current", StringComparison.OrdinalIgnoreCase)));
        var statusLabel = hasBlockers
            ? "Blocked"
            : finalStageAllowed
                ? (executionSnapshot.IsTradeMasterArmed ? "Ready" : "Pending arm")
                : livePilotAllowed
                    ? "Pilot ready"
                    : "Not ready";
        var statusTone = hasBlockers
            ? "critical"
            : finalStageAllowed
                ? (executionSnapshot.IsTradeMasterArmed ? "healthy" : "warning")
                : livePilotAllowed
                    ? "warning"
                    : "critical";
        var summary = hasBlockers
            ? $"{blockingReasons.Count} rollout blocker var. Ilk neden {blockingReasons.First().ReasonCode}."
            : finalStageAllowed
                ? (executionSnapshot.IsTradeMasterArmed
                    ? "Genel aktivasyon kapilari temiz. Rollback ve emergency yuzeyi guarded aksiyonlarla hazir."
                    : "Genel aktivasyon kapilari temiz; son adim TradeMaster arm komutudur.")
                : livePilotAllowed
                    ? "Sinirli canli pilot hazir; genisleme ve genel aktivasyon ayri guard'larla degerlendirilir."
                    : "Rollout closure kapilari eksik; block reason listesi izlenmelidir.";
        var rollbackSummary = executionSnapshot.IsTradeMasterArmed
            ? "Rollback icin once TradeMaster'i disarm edin; gerekirse Soft Halt veya Emergency Stop paneline gecin, sonra incident/audit merkezinde etkileri dogrulayin."
            : "Sistem zaten disarmed. Rollback sonrasi beklenen durum: TradeMaster=Disarmed, rollout stage'leri yeniden degerlendirilir, incident/audit zinciri acik kalir.";
        var emergencySummary = $"Kill switch={(executionSnapshot.IsTradeMasterArmed ? "Clear" : "Engaged")} · Soft halt={(globalSystemStateSnapshot.State == GlobalSystemStateKind.SoftHalt ? "Active" : "Clear")} · Emergency stop={(globalSystemStateSnapshot.State == GlobalSystemStateKind.FullHalt ? "Active" : "Clear")}.";

        return new AdminRolloutClosureCenterViewModel(
            IsAccessible: true,
            StatusLabel: statusLabel,
            StatusTone: statusTone,
            Summary: summary,
            RefreshedAtUtcLabel: FormatUtc(evaluatedAtUtc),
            Stages: stages,
            MandatoryGates: mandatoryGates,
            GoLiveChecklist: goLiveChecklist,
            BlockingReasons: blockingReasons,
            RelatedLinks: relatedLinks,
            RollbackSummary: rollbackSummary,
            EmergencySummary: emergencySummary);
    }

    private static IReadOnlyCollection<AdminRolloutStatusItemViewModel> BuildRolloutMandatoryGates(
        AdminActivationControlCenterViewModel activationControlCenter,
        GlobalExecutionSwitchSnapshot executionSnapshot,
        DegradedModeSnapshot driftGuardSnapshot,
        MonitoringDashboardSnapshot monitoringDashboard,
        IReadOnlyCollection<ApiCredentialAdminSummary> credentialSummaries,
        IReadOnlyCollection<AdminRolloutEvidenceInput> evidenceInputs)
    {
        var evidenceByKey = evidenceInputs.ToDictionary(item => item.Key, StringComparer.OrdinalIgnoreCase);
        var items = new List<AdminRolloutStatusItemViewModel>
        {
            CreateRolloutEvidenceGate(evidenceByKey, "build-clean", "Build temiz", "BuildEvidenceMissing", "dotnet build CoinBot.sln"),
            CreateRolloutEvidenceGate(evidenceByKey, "unit-tests-clean", "Unit test temiz", "UnitTestEvidenceMissing", "dotnet test CoinBot.UnitTests"),
            CreateRolloutEvidenceGate(evidenceByKey, "ui-smoke-clean", "UI smoke temiz", "UiSmokeEvidenceMissing", "SettingsBrowserSmoke"),
            CreateRolloutEvidenceGate(evidenceByKey, "pilot-lifecycle-clean", "Pilot lifecycle smoke temiz", "PilotLifecycleEvidenceMissing", "PilotLifecycleRuntimeSmoke"),
            BuildRolloutReadinessGate(activationControlCenter),
            BuildRolloutExchangeGate(executionSnapshot, credentialSummaries),
            BuildRolloutContinuityGate(driftGuardSnapshot, monitoringDashboard),
            CreateRolloutEvidenceGate(evidenceByKey, "kill-switch-tested", "Kill switch test edildi", "KillSwitchEvidenceMissing", "UiLiveBrowserSmoke")
        };

        return items;
    }
    private static IReadOnlyCollection<AdminRolloutStatusItemViewModel> BuildGoLiveChecklist(
        AdminActivationControlCenterViewModel activationControlCenter,
        GlobalExecutionSwitchSnapshot executionSnapshot,
        GlobalSystemStateSnapshot globalSystemStateSnapshot,
        IReadOnlyCollection<ApiCredentialAdminSummary> credentialSummaries,
        GlobalPolicySnapshot globalPolicySnapshot,
        BotExecutionPilotOptions pilotOptions,
        LogCenterPageSnapshot? logCenterSnapshot,
        LogCenterRetentionSnapshot? retentionSnapshot)
    {
        var (environmentReady, environmentSummary, environmentReasonCode) = EvaluateEnvironmentReadiness(executionSnapshot, credentialSummaries);
        var hasSingleScope = pilotOptions.AllowedUserIds.Length == 1 &&
                             pilotOptions.AllowedBotIds.Length == 1 &&
                             pilotOptions.AllowedSymbols.Length == 1;
        var singleScopeSummary = hasSingleScope
            ? $"Pilot scope tek user / tek bot / tek symbol ile sinirli. User={pilotOptions.AllowedUserIds[0]}, Bot={pilotOptions.AllowedBotIds[0]}, Symbol={pilotOptions.AllowedSymbols[0]}."
            : "Pilot scope tek user / tek bot / tek symbol olarak sinirlanmamis.";
        var hasSmallNotional = pilotOptions.TryResolveMaxPilotOrderNotional(out var pilotNotional) &&
                               pilotNotional > 0m &&
                               (!globalPolicySnapshot.Policy.ExecutionGuardPolicy.MaxOrderNotional.HasValue ||
                                pilotNotional <= globalPolicySnapshot.Policy.ExecutionGuardPolicy.MaxOrderNotional.Value);
        var notionalSummary = hasSmallNotional
            ? $"Pilot notional cap {pilotNotional.ToString("0.##", CultureInfo.InvariantCulture)} ve global guard ile uyumlu."
            : "Pilot notional cap eksik, gecersiz veya global guard limitini asiyor.";
        var emergencyReady = activationControlCenter.CriticalSwitches.Any(item => item.Key == "kill-switch") &&
                             activationControlCenter.CriticalSwitches.Any(item => item.Key == "soft-halt") &&
                             activationControlCenter.CriticalSwitches.Any(item => item.Key == "emergency-stop");
        var auditVisible = logCenterSnapshot is not null && !logCenterSnapshot.HasError;
        var logVisible = retentionSnapshot is not null && auditVisible;

        return new[]
        {
            CreateRolloutStatusItem(
                "trade-master-armed",
                "TradeMaster = Armed",
                executionSnapshot.IsTradeMasterArmed ? "Pass" : "Blocked",
                executionSnapshot.IsTradeMasterArmed ? "healthy" : "critical",
                executionSnapshot.IsTradeMasterArmed ? "TradeMaster armed; global kill switch clear." : "TradeMaster disarmed; rollout aktivasyonu verilemez.",
                executionSnapshot.IsTradeMasterArmed ? "TradeMasterArmed" : "TradeMasterDisarmed",
                "GlobalExecutionSwitchService"),
            CreateRolloutStatusItem(
                "pilot-activation-enabled",
                "PilotActivation = true",
                pilotOptions.PilotActivationEnabled ? "Pass" : "Blocked",
                pilotOptions.PilotActivationEnabled ? "healthy" : "critical",
                pilotOptions.PilotActivationEnabled ? "PilotActivationEnabled=true." : "PilotActivationEnabled=false; rollout pilot yolu kapali.",
                pilotOptions.PilotActivationEnabled ? "PilotActivationEnabled" : "PilotActivationDisabled",
                "BotExecutionPilotOptions"),
            CreateRolloutStatusItem(
                "environment-match",
                "Dogru environment",
                environmentReady ? "Pass" : "Blocked",
                environmentReady ? "healthy" : "critical",
                environmentSummary,
                environmentReasonCode,
                "GlobalExecutionSwitchService + ApiCredentialValidationService"),
            CreateRolloutStatusItem(
                "single-scope",
                "Tek bot / tek symbol",
                hasSingleScope ? "Pass" : "Blocked",
                hasSingleScope ? "healthy" : "critical",
                singleScopeSummary,
                hasSingleScope ? "PilotScopeSingle" : "PilotScopeViolation",
                "BotExecutionPilotOptions"),
            CreateRolloutStatusItem(
                "small-notional",
                "Kucuk notional",
                hasSmallNotional ? "Pass" : "Blocked",
                hasSmallNotional ? "healthy" : "critical",
                notionalSummary,
                hasSmallNotional ? "PilotNotionalBounded" : "PilotNotionalViolation",
                "BotExecutionPilotOptions + GlobalPolicySnapshot"),
            CreateRolloutStatusItem(
                "emergency-ready",
                "Emergency stop hazir",
                emergencyReady ? "Pass" : "Blocked",
                emergencyReady ? "healthy" : "critical",
                emergencyReady ? "Kill switch, soft halt ve emergency stop yuzeyi gorunur." : "Emergency stop yuzeyi eksik; rollout fail-closed bloklanir.",
                emergencyReady ? "EmergencySurfaceReady" : "EmergencySurfaceMissing",
                "ActivationControlCenter"),
            CreateRolloutStatusItem(
                "incident-audit-visible",
                "Incident / audit gorunur",
                auditVisible ? "Pass" : "Blocked",
                auditVisible ? "healthy" : "critical",
                auditVisible ? "Incident / Audit / Decision Center rollout kanit zinciri icin erisilebilir." : "Incident / audit read-model unavailable; rollout kanit zinciri eksik.",
                auditVisible ? "IncidentAuditVisible" : "IncidentAuditVisibilityInsufficient",
                "LogCenterReadModelService"),
            CreateRolloutStatusItem(
                "log-retention-visible",
                "Log retention / trace gorunur",
                logVisible ? "Pass" : "Blocked",
                logVisible ? "healthy" : "critical",
                logVisible ? "Retention ve trace gorunurlugu rollout sonrasi tanilama icin hazir." : "Log retention snapshot veya trace read-model unavailable.",
                logVisible ? "TraceRetentionVisible" : "TraceRetentionUnavailable",
                "LogCenterRetentionService")
        };
    }

    private static IReadOnlyCollection<AdminRolloutStatusItemViewModel> BuildRolloutBlockingReasons(
        IReadOnlyCollection<AdminRolloutStatusItemViewModel> mandatoryGates,
        IReadOnlyCollection<AdminRolloutStatusItemViewModel> goLiveChecklist)
    {
        return mandatoryGates
            .Concat(goLiveChecklist)
            .Where(item => !IsPassingRolloutItem(item))
            .GroupBy(item => item.ReasonCode, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First() with { StatusLabel = "Blocked", Tone = group.First().Tone.Equals("healthy", StringComparison.OrdinalIgnoreCase) ? "warning" : group.First().Tone })
            .ToArray();
    }

    private static IReadOnlyCollection<AdminRolloutStageViewModel> BuildRolloutStages(
        GlobalExecutionSwitchSnapshot executionSnapshot,
        BotExecutionPilotOptions pilotOptions,
        IReadOnlyCollection<ApiCredentialAdminSummary> credentialSummaries,
        GlobalSystemStateSnapshot globalSystemStateSnapshot,
        GlobalPolicySnapshot globalPolicySnapshot,
        IReadOnlyCollection<AdminRolloutStatusItemViewModel> blockingReasons,
        LogCenterPageSnapshot? logCenterSnapshot,
        DateTime evaluatedAtUtc)
    {
        var blockerCodes = blockingReasons.Select(item => item.ReasonCode).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var hasBaseBlockers = blockerCodes.Length > 0;
        var hasTestnetCredential = HasMatchingCredential(credentialSummaries, preferDemoEnvironment: true, requireWritable: false);
        var hasLiveCredential = HasMatchingCredential(credentialSummaries, preferDemoEnvironment: false, requireWritable: true);
        var hasSingleScope = pilotOptions.AllowedUserIds.Length == 1 && pilotOptions.AllowedBotIds.Length == 1 && pilotOptions.AllowedSymbols.Length == 1;
        var hasSmallNotional = pilotOptions.TryResolveMaxPilotOrderNotional(out var pilotNotional) &&
                               pilotNotional > 0m &&
                               (!globalPolicySnapshot.Policy.ExecutionGuardPolicy.MaxOrderNotional.HasValue || pilotNotional <= globalPolicySnapshot.Policy.ExecutionGuardPolicy.MaxOrderNotional.Value);
        var changedBy = logCenterSnapshot?.Entries.OrderByDescending(item => item.CreatedAtUtc).Select(item => item.UserId).FirstOrDefault(item => !string.IsNullOrWhiteSpace(item))
            ?? globalSystemStateSnapshot.UpdatedByUserId
            ?? "Unavailable";

        return new[]
        {
            BuildRolloutStage(
                "stage-demo",
                "Demo/Testnet",
                executionSnapshot.DemoModeEnabled && hasTestnetCredential,
                executionSnapshot.DemoModeEnabled,
                hasBaseBlockers,
                blockerCodes,
                executionSnapshot.DemoModeEnabled && hasTestnetCredential ? Array.Empty<string>() : new[] { !executionSnapshot.DemoModeEnabled ? "EnvironmentMismatch" : "ExchangeValidationFailed" },
                "Demo/Testnet asamasi mevcut config ile calistirilabilir.",
                "Demo/Testnet asamasi icin demo mode ve testnet credential birlikte gereklidir.",
                evaluatedAtUtc,
                changedBy),
            BuildRolloutStage(
                "stage-single-pilot",
                "Tek user + tek bot + tek symbol pilot",
                pilotOptions.PilotActivationEnabled && hasSingleScope && hasSmallNotional,
                hasSingleScope,
                hasBaseBlockers,
                blockerCodes,
                BuildScopeBlockers(pilotOptions, hasSmallNotional),
                "Pilot scope tek user / tek bot / tek symbol ile sinirli ve rollout icin uygun.",
                "Pilot scope, notional veya activation guard tekil pilot kosulunu saglamiyor.",
                evaluatedAtUtc,
                changedBy),
            BuildRolloutStage(
                "stage-live-pilot",
                "Sinirli canli pilot",
                !executionSnapshot.DemoModeEnabled && executionSnapshot.HasLiveModeApproval && pilotOptions.PilotActivationEnabled && hasSingleScope && hasSmallNotional && hasLiveCredential,
                !executionSnapshot.DemoModeEnabled,
                hasBaseBlockers,
                blockerCodes,
                BuildLivePilotBlockers(executionSnapshot, pilotOptions, hasSingleScope, hasSmallNotional, hasLiveCredential),
                "Canli pilot icin gerekli live approval, scope ve credential guardlari temiz.",
                "Sinirli canli pilot once live approval, writable credential ve tekil pilot scope ister.",
                evaluatedAtUtc,
                changedBy),
            BuildRolloutStage(
                "stage-gradual",
                "Kademeli genisleme",
                !executionSnapshot.DemoModeEnabled && executionSnapshot.HasLiveModeApproval && hasLiveCredential &&
                (pilotOptions.AllowedUserIds.Length > 1 || pilotOptions.AllowedBotIds.Length > 1 || pilotOptions.AllowedSymbols.Length > 1),
                !executionSnapshot.DemoModeEnabled && (pilotOptions.AllowedUserIds.Length > 1 || pilotOptions.AllowedBotIds.Length > 1 || pilotOptions.AllowedSymbols.Length > 1),
                hasBaseBlockers,
                blockerCodes,
                BuildGradualRolloutBlockers(executionSnapshot, hasLiveCredential, pilotOptions),
                "Pilot scope genisletilmis ve live guard'lar aktif; kademeli rollout icin uygun.",
                "Kademeli genisleme once live mode, writable credential ve genisletilmis scope ister.",
                evaluatedAtUtc,
                changedBy),
            BuildRolloutStage(
                "stage-general",
                "Genel aktivasyon",
                executionSnapshot.IsTradeMasterArmed && !executionSnapshot.DemoModeEnabled && executionSnapshot.HasLiveModeApproval && hasLiveCredential && globalSystemStateSnapshot.State == GlobalSystemStateKind.Active &&
                (pilotOptions.AllowedUserIds.Length > 1 || pilotOptions.AllowedBotIds.Length > 1 || pilotOptions.AllowedSymbols.Length > 1),
                executionSnapshot.IsTradeMasterArmed,
                hasBaseBlockers,
                blockerCodes,
                BuildGeneralActivationBlockers(executionSnapshot, globalSystemStateSnapshot, hasLiveCredential, pilotOptions),
                "Genel aktivasyon icin TradeMaster armed, live approval ve genisletilmis rollout scope hazir.",
                "Genel aktivasyon once live approval, armed state ve pilot scope genislemesi ister.",
                evaluatedAtUtc,
                changedBy)
        };
    }
    private static IReadOnlyCollection<AdminRolloutLinkedItemViewModel> BuildRolloutLinks(
        AdminActivationControlCenterViewModel activationControlCenter,
        GlobalExecutionSwitchSnapshot executionSnapshot,
        GlobalSystemStateSnapshot globalSystemStateSnapshot,
        LogCenterPageSnapshot? logCenterSnapshot,
        DateTime evaluatedAtUtc)
    {
        var latestDecisionEntry = logCenterSnapshot?.Entries
            .Where(item => !string.IsNullOrWhiteSpace(item.DecisionReasonCode))
            .OrderByDescending(item => item.DecisionAtUtc ?? item.CreatedAtUtc)
            .FirstOrDefault();
        var latestCriticalAudit = logCenterSnapshot?.Entries
            .Where(item => ContainsToken(item.Kind, "Audit") || string.Equals(item.Tone, "critical", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.CreatedAtUtc)
            .FirstOrDefault();
        var latestIncident = logCenterSnapshot?.Entries
            .Where(item => !string.IsNullOrWhiteSpace(item.IncidentReference) || ContainsToken(item.Kind, "Incident"))
            .OrderByDescending(item => item.CreatedAtUtc)
            .FirstOrDefault();

        return new[]
        {
            new AdminRolloutLinkedItemViewModel(
                "Last decision",
                $"{activationControlCenter.LastDecision.Code} - {activationControlCenter.LastDecision.Summary}",
                activationControlCenter.LastDecision.Tone,
                latestDecisionEntry is null ? "/admin/Audit" : BuildAuditFocusHref(latestDecisionEntry),
                activationControlCenter.LastDecision.EvaluatedAtUtcLabel),
            new AdminRolloutLinkedItemViewModel(
                "Last critical audit",
                latestCriticalAudit?.Summary ?? "Critical audit trail görünür olmadan rollout pass sayilmaz.",
                latestCriticalAudit?.Tone ?? "warning",
                latestCriticalAudit is null ? "/admin/Audit" : BuildAuditFocusHref(latestCriticalAudit),
                latestCriticalAudit is null ? FormatUtc(evaluatedAtUtc) : FormatUtc(latestCriticalAudit.CreatedAtUtc)),
            new AdminRolloutLinkedItemViewModel(
                "Incident / trace",
                latestIncident?.Summary ?? "Incident / trace merkezi rollout tanilama zinciri icin referans noktasi olarak kullanilir.",
                latestIncident?.Tone ?? "info",
                latestIncident is null ? "/admin/Audit" : BuildAuditFocusHref(latestIncident),
                latestIncident is null ? $"State={globalSystemStateSnapshot.State}" : FormatUtc(latestIncident.CreatedAtUtc)),
            new AdminRolloutLinkedItemViewModel(
                "Rollback / emergency",
                executionSnapshot.IsTradeMasterArmed
                    ? "Rollback icin once TradeMaster disarm, sonra gerekirse Soft Halt veya Emergency Stop uygulayin."
                    : "Emergency paneli ve guarded deactivation yüzeyi rollout ekranina bagli olarak hazir.",
                executionSnapshot.IsTradeMasterArmed ? "warning" : "info",
                "/admin/Settings#cb_admin_settings_activation",
                $"Kill={(executionSnapshot.IsTradeMasterArmed ? "Clear" : "Engaged")} · State={globalSystemStateSnapshot.State}")
        };
    }

    private static AdminRolloutStatusItemViewModel BuildRolloutReadinessGate(AdminActivationControlCenterViewModel activationControlCenter)
    {
        if (activationControlCenter.BlockingItems.Count == 0)
        {
            return CreateRolloutStatusItem(
                "readiness-green",
                "Readiness yesil",
                "Pass",
                "healthy",
                "Activation readiness checklist pass; unknown veya fail item yok.",
                "ReadinessReady",
                "ActivationControlCenter");
        }

        var blocker = activationControlCenter.BlockingItems.First();
        return CreateRolloutStatusItem(
            "readiness-green",
            "Readiness yesil",
            "Blocked",
            "critical",
            blocker.Summary,
            blocker.ReasonCode,
            blocker.SourceHint);
    }

    private static AdminRolloutStatusItemViewModel BuildRolloutExchangeGate(
        GlobalExecutionSwitchSnapshot executionSnapshot,
        IReadOnlyCollection<ApiCredentialAdminSummary> credentialSummaries)
    {
        var (ready, summary, reasonCode) = EvaluateEnvironmentReadiness(executionSnapshot, credentialSummaries);
        return CreateRolloutStatusItem(
            "exchange-validation",
            "Exchange validation dogru",
            ready ? "Pass" : "Blocked",
            ready ? "healthy" : "critical",
            summary,
            reasonCode,
            "ApiCredentialValidationService");
    }

    private static AdminRolloutStatusItemViewModel BuildRolloutContinuityGate(
        DegradedModeSnapshot driftGuardSnapshot,
        MonitoringDashboardSnapshot monitoringDashboard)
    {
        var latestHandoff = monitoringDashboard.MarketScanner.LatestHandoff;
        var noHandoffEvidence = latestHandoff.CompletedAtUtc is null &&
                                string.IsNullOrWhiteSpace(latestHandoff.DecisionReasonCode) &&
                                string.IsNullOrWhiteSpace(latestHandoff.BlockerCode);

        if (!driftGuardSnapshot.IsPersisted || noHandoffEvidence)
        {
            return CreateRolloutStatusItem(
                "continuity-clean",
                "No stale / no continuity gap",
                "Blocked",
                "critical",
                "Market freshness veya continuity icin yeterli rollout kaniti yok; unknown durum pass sayilmaz.",
                "ContinuityEvidenceMissing",
                "MonitoringDashboardSnapshot");
        }

        if (!string.IsNullOrWhiteSpace(latestHandoff.MarketDataStaleReason))
        {
            return CreateRolloutStatusItem(
                "continuity-clean",
                "No stale / no continuity gap",
                "Blocked",
                "critical",
                $"Market data stale: {latestHandoff.MarketDataStaleReason}.",
                "MarketDataStale",
                "MonitoringDashboardSnapshot");
        }

        if ((latestHandoff.ContinuityGapCount ?? 0) > 0 &&
            !string.Equals(latestHandoff.ContinuityState, "Recovered", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(latestHandoff.ContinuityState, "Healthy", StringComparison.OrdinalIgnoreCase))
        {
            return CreateRolloutStatusItem(
                "continuity-clean",
                "No stale / no continuity gap",
                "Blocked",
                "critical",
                $"Continuity gap state={latestHandoff.ContinuityState ?? "Unknown"}; gapCount={latestHandoff.ContinuityGapCount ?? 0}.",
                "ContinuityGapDetected",
                "MonitoringDashboardSnapshot");
        }

        if (!driftGuardSnapshot.IsNormal)
        {
            return CreateRolloutStatusItem(
                "continuity-clean",
                "No stale / no continuity gap",
                "Blocked",
                "critical",
                $"Drift guard {driftGuardSnapshot.ReasonCode}; latest age {driftGuardSnapshot.LatestDataAgeMilliseconds?.ToString() ?? "n/a"} ms.",
                driftGuardSnapshot.ReasonCode.ToString(),
                "DataLatencyCircuitBreaker");
        }

        return CreateRolloutStatusItem(
            "continuity-clean",
            "No stale / no continuity gap",
            "Pass",
            "healthy",
            "Market freshness ve continuity guard sinyalleri rollout icin temiz.",
            "ContinuityClean",
            "MonitoringDashboardSnapshot");
    }

    private static AdminRolloutStatusItemViewModel CreateRolloutEvidenceGate(
        IReadOnlyDictionary<string, AdminRolloutEvidenceInput> evidenceByKey,
        string key,
        string label,
        string missingReasonCode,
        string sourceLabel)
    {
        if (!evidenceByKey.TryGetValue(key, out var evidence))
        {
            return CreateRolloutStatusItem(
                key,
                label,
                "Blocked",
                "critical",
                $"{label} icin son-known evidence bulunamadi; rollout pass sayilmaz.",
                missingReasonCode,
                sourceLabel);
        }

        return CreateRolloutStatusItem(
            key,
            label,
            evidence.IsPassing ? "Pass" : "Blocked",
            evidence.IsPassing ? "healthy" : "critical",
            evidence.Summary,
            evidence.ReasonCode,
            evidence.SourceLabel);
    }
    private static AdminRolloutStageViewModel BuildRolloutStage(
        string key,
        string label,
        bool isAllowed,
        bool isCurrent,
        bool hasBaseBlockers,
        IReadOnlyCollection<string> baseBlockers,
        IReadOnlyCollection<string> localBlockers,
        string allowedSummary,
        string blockedSummary,
        DateTime evaluatedAtUtc,
        string changedBy)
    {
        var blockers = (hasBaseBlockers ? baseBlockers.Concat(localBlockers) : localBlockers)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var statusLabel = blockers.Length == 0
            ? (isCurrent ? "Current" : isAllowed ? "Allowed" : "Not ready")
            : hasBaseBlockers ? "Blocked" : "Not ready";
        var tone = blockers.Length == 0
            ? (isCurrent ? "healthy" : isAllowed ? "info" : "warning")
            : hasBaseBlockers ? "critical" : "warning";
        var summary = blockers.Length == 0 ? allowedSummary : blockedSummary;

        return CreateRolloutStage(
            key,
            label,
            statusLabel,
            tone,
            summary,
            blockers.Length == 0 ? "None" : string.Join(" · ", blockers.Take(4)),
            evaluatedAtUtc,
            changedBy);
    }

    private static AdminRolloutStageViewModel CreateRolloutStage(
        string key,
        string label,
        string statusLabel,
        string tone,
        string summary,
        string blockingReasons,
        DateTime evaluatedAtUtc,
        string changedBy)
    {
        return new AdminRolloutStageViewModel(
            key,
            label,
            statusLabel,
            tone,
            summary,
            blockingReasons,
            FormatUtc(evaluatedAtUtc),
            string.IsNullOrWhiteSpace(changedBy) ? "Unavailable" : changedBy);
    }

    private static AdminRolloutStatusItemViewModel CreateRolloutStatusItem(
        string key,
        string label,
        string statusLabel,
        string tone,
        string summary,
        string reasonCode,
        string sourceLabel)
    {
        return new AdminRolloutStatusItemViewModel(
            key,
            label,
            statusLabel,
            tone,
            summary,
            string.IsNullOrWhiteSpace(reasonCode) ? "Unavailable" : reasonCode,
            sourceLabel);
    }

    private static bool IsPassingRolloutItem(AdminRolloutStatusItemViewModel item)
    {
        return string.Equals(item.StatusLabel, "Pass", StringComparison.OrdinalIgnoreCase);
    }

    private static (bool IsReady, string Summary, string ReasonCode) EvaluateEnvironmentReadiness(
        GlobalExecutionSwitchSnapshot executionSnapshot,
        IReadOnlyCollection<ApiCredentialAdminSummary> credentialSummaries)
    {
        var hasTestnetCredential = HasMatchingCredential(credentialSummaries, preferDemoEnvironment: true, requireWritable: false);
        var hasLiveCredential = HasMatchingCredential(credentialSummaries, preferDemoEnvironment: false, requireWritable: true);

        if (executionSnapshot.DemoModeEnabled)
        {
            return hasTestnetCredential
                ? (true, "Demo/Testnet mode icin en az bir validated testnet credential bulundu.", "DemoEnvironmentReady")
                : (false, "Demo/Testnet mode secili ancak validated testnet credential bulunamadi.", "EnvironmentMismatch");
        }

        if (!executionSnapshot.HasLiveModeApproval)
        {
            return (false, "Live mode secili fakat approval reference okunamadi.", "LiveModeApprovalMissing");
        }

        return hasLiveCredential
            ? (true, "Live mode icin validated writable credential ve approval reference gorunur.", "LiveEnvironmentReady")
            : (false, "Live mode icin validated writable credential bulunamadi.", "ExchangeValidationFailed");
    }

    private static bool HasMatchingCredential(
        IReadOnlyCollection<ApiCredentialAdminSummary> credentialSummaries,
        bool preferDemoEnvironment,
        bool requireWritable)
    {
        foreach (var summary in credentialSummaries)
        {
            if (!string.Equals(summary.ValidationStatus, "Valid", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var parsedSummary = ParsePermissionSummary(summary.PermissionSummary);
            var environmentLabel = ResolveEnvironmentLabel(parsedSummary);
            var accessLabel = ResolveAccessLabel(summary, parsedSummary);
            var isDemoEnvironment = ContainsToken(environmentLabel, "Test") || ContainsToken(environmentLabel, "Demo");
            var isWritable = accessLabel.Contains("Writable", StringComparison.OrdinalIgnoreCase);

            if (preferDemoEnvironment == isDemoEnvironment && (!requireWritable || isWritable))
            {
                return true;
            }
        }

        return false;
    }

    private static IReadOnlyCollection<string> BuildScopeBlockers(BotExecutionPilotOptions pilotOptions, bool hasSmallNotional)
    {
        var blockers = new List<string>();

        if (!pilotOptions.PilotActivationEnabled)
        {
            blockers.Add("PilotActivationDisabled");
        }

        if (pilotOptions.AllowedUserIds.Length != 1 || pilotOptions.AllowedBotIds.Length != 1 || pilotOptions.AllowedSymbols.Length != 1)
        {
            blockers.Add("PilotScopeViolation");
        }

        if (!hasSmallNotional)
        {
            blockers.Add("PilotNotionalViolation");
        }

        return blockers;
    }

    private static IReadOnlyCollection<string> BuildLivePilotBlockers(
        GlobalExecutionSwitchSnapshot executionSnapshot,
        BotExecutionPilotOptions pilotOptions,
        bool hasSingleScope,
        bool hasSmallNotional,
        bool hasLiveCredential)
    {
        var blockers = new List<string>();
        if (executionSnapshot.DemoModeEnabled)
        {
            blockers.Add("EnvironmentMismatch");
        }

        if (!executionSnapshot.HasLiveModeApproval)
        {
            blockers.Add("LiveModeApprovalMissing");
        }

        if (!pilotOptions.PilotActivationEnabled)
        {
            blockers.Add("PilotActivationDisabled");
        }

        if (!hasSingleScope)
        {
            blockers.Add("PilotScopeViolation");
        }

        if (!hasSmallNotional)
        {
            blockers.Add("PilotNotionalViolation");
        }

        if (!hasLiveCredential)
        {
            blockers.Add("ExchangeValidationFailed");
        }

        return blockers;
    }

    private static IReadOnlyCollection<string> BuildGradualRolloutBlockers(
        GlobalExecutionSwitchSnapshot executionSnapshot,
        bool hasLiveCredential,
        BotExecutionPilotOptions pilotOptions)
    {
        var blockers = new List<string>();

        if (executionSnapshot.DemoModeEnabled)
        {
            blockers.Add("EnvironmentMismatch");
        }

        if (!executionSnapshot.HasLiveModeApproval)
        {
            blockers.Add("LiveModeApprovalMissing");
        }

        if (!hasLiveCredential)
        {
            blockers.Add("ExchangeValidationFailed");
        }

        if (pilotOptions.AllowedUserIds.Length <= 1 && pilotOptions.AllowedBotIds.Length <= 1 && pilotOptions.AllowedSymbols.Length <= 1)
        {
            blockers.Add("PilotScopeStillSingle");
        }

        return blockers;
    }

    private static IReadOnlyCollection<string> BuildGeneralActivationBlockers(
        GlobalExecutionSwitchSnapshot executionSnapshot,
        GlobalSystemStateSnapshot globalSystemStateSnapshot,
        bool hasLiveCredential,
        BotExecutionPilotOptions pilotOptions)
    {
        var blockers = new List<string>();

        if (executionSnapshot.DemoModeEnabled)
        {
            blockers.Add("EnvironmentMismatch");
        }

        if (!executionSnapshot.HasLiveModeApproval)
        {
            blockers.Add("LiveModeApprovalMissing");
        }

        if (!executionSnapshot.IsTradeMasterArmed)
        {
            blockers.Add("TradeMasterDisarmed");
        }

        if (globalSystemStateSnapshot.State != GlobalSystemStateKind.Active)
        {
            blockers.Add(globalSystemStateSnapshot.ReasonCode);
        }

        if (!hasLiveCredential)
        {
            blockers.Add("ExchangeValidationFailed");
        }

        if (pilotOptions.AllowedUserIds.Length <= 1 && pilotOptions.AllowedBotIds.Length <= 1 && pilotOptions.AllowedSymbols.Length <= 1)
        {
            blockers.Add("PilotScopeStillSingle");
        }

        return blockers;
    }

    private static string BuildAuditFocusHref(LogCenterEntrySnapshot entry)
    {
        var focus = entry.Reference;

        if (string.IsNullOrWhiteSpace(focus))
        {
            focus = entry.ApprovalReference ?? entry.IncidentReference ?? entry.DecisionId ?? entry.ExecutionAttemptId ?? entry.CorrelationId;
        }

        return string.IsNullOrWhiteSpace(focus)
            ? "/admin/Audit"
            : $"/admin/Audit?focus={Uri.EscapeDataString(focus)}";
    }

    private static string FormatUtc(DateTime? value)
    {
        return value?.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'") ?? "n/a";
    }
}


