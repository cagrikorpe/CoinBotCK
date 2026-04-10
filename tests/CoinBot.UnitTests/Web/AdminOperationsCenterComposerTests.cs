using System.Linq;
using CoinBot.Application.Abstractions.Administration;
using CoinBot.Application.Abstractions.Execution;
using CoinBot.Application.Abstractions.Exchange;
using CoinBot.Application.Abstractions.Monitoring;
using CoinBot.Application.Abstractions.Policy;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Jobs;
using CoinBot.Web.ViewModels.Admin;

namespace CoinBot.UnitTests.Web;

public sealed class AdminOperationsCenterComposerTests
{
    [Fact]
    public void CreateAccessDenied_ReturnsFailClosedBlockedModel()
    {
        var model = AdminOperationsCenterComposer.CreateAccessDenied(new DateTime(2026, 4, 8, 12, 0, 0, DateTimeKind.Utc));

        Assert.False(model.IsAccessible);
        Assert.Equal("Super Admin gerekli", model.AccessTitle);
        Assert.Single(model.SummaryCards);
        Assert.Equal("Blocked", model.RuntimeHealthCenter.StatusLabel);
    }

    [Fact]
    public void Compose_WhenRuntimeIsUnknown_FailsClosed_AndKeepsCredentialMasked()
    {
        var model = AdminOperationsCenterComposer.Compose(
            CreateActivationModel(),
            MonitoringDashboardSnapshot.Empty(new DateTime(2026, 4, 8, 12, 0, 0, DateTimeKind.Utc)),
            CreateClockSnapshot(),
            CreateDriftSnapshot(isPersisted: false),
            AdminUsersPageSnapshot.Empty(new DateTime(2026, 4, 8, 12, 0, 0, DateTimeKind.Utc)),
            AdminBotOperationsPageSnapshot.Empty(new DateTime(2026, 4, 8, 12, 0, 0, DateTimeKind.Utc)),
            [new ApiCredentialAdminSummary(Guid.NewGuid(), "user-1", "Binance", "Primary", false, "fp-****-7890", "Invalid", "Env=Testnet;Spot=True;Futures=True;Trade=True", new DateTime(2026, 4, 8, 11, 30, 0, DateTimeKind.Utc), "Validation failed")],
            GlobalPolicySnapshot.CreateDefault(new DateTime(2026, 4, 8, 12, 0, 0, DateTimeKind.Utc)),
            new BotExecutionPilotOptions { PilotActivationEnabled = true, MaxPilotOrderNotional = "250" },
            null,
            new GlobalExecutionSwitchSnapshot(TradeMasterSwitchState.Disarmed, true, true),
            new GlobalSystemStateSnapshot(GlobalSystemStateKind.Active, "SYSTEM_ACTIVE", null, "AdminPortal", null, false, null, new DateTime(2026, 4, 8, 11, 59, 0, DateTimeKind.Utc), "super-admin", "ip:masked", 1, true),
            true,
            new DateTime(2026, 4, 8, 12, 1, 0, DateTimeKind.Utc));

        Assert.True(model.IsAccessible);
        Assert.Equal("critical", model.RuntimeHealthCenter.StatusTone);
        Assert.Equal("Blocked", model.PrimaryFlow.Setup.StatusLabel);
        Assert.True(model.PrimaryFlow.Setup.IsVisible);
        Assert.True(model.PrimaryFlow.Setup.IsAccessible);
        var activateAction = Assert.Single(model.PrimaryFlow.Activation.Actions, item => item.Key == "activate");
        var activationActionDebug = string.Join(" | ", model.PrimaryFlow.Activation.Actions.Select(item => $"{item.Key}:{item.IsEnabled}:{item.BlockedReason}"));
        Assert.False(activateAction.IsEnabled, activationActionDebug);
        Assert.Equal("Exchange bagli degil", activateAction.BlockedReason);
        Assert.Equal("Exchange bagli degil", model.PrimaryFlow.Setup.PrimaryMessage);
        Assert.Contains(model.RuntimeHealthCenter.Signals, item => item.Code == "WorkerHeartbeatUnavailable");
        var credential = Assert.Single(model.ExchangeGovernanceCenter.Accounts);
        Assert.Equal("fp-****-7890", credential.FingerprintLabel);
        Assert.Equal("Testnet", credential.EnvironmentLabel);
        Assert.Equal("Spot + Futures", credential.CapabilityLabel);
        Assert.Contains("Writable", credential.AccessLabel);
    }

    [Fact]
    public void Compose_SegmentsProblemUsersAndBots_FromExistingSnapshotTones()
    {
        var users = new AdminUsersPageSnapshot(
            null,
            null,
            null,
            Array.Empty<AdminStatTileSnapshot>(),
            [new AdminUserListItemSnapshot("user-1", "Ops User", "ops.user", null, "Review", "warning", "MFA Off", "critical", "OpsAdmin", "Live", "warning", 2, 1, new DateTime(2026, 4, 8, 11, 0, 0, DateTimeKind.Utc), "1h ago", "warning")],
            new DateTime(2026, 4, 8, 12, 0, 0, DateTimeKind.Utc));
        var bots = new AdminBotOperationsPageSnapshot(
            null,
            null,
            null,
            Array.Empty<AdminStatTileSnapshot>(),
            [new AdminBotOperationSnapshot("bot-1", "Mean Reverter", "user-1", "Ops User", "Blocked", "critical", "Demo", "warning", "mean-revert", "High", "warning", "Cooldown active", "stale heartbeat and repeated error", 1, 0)],
            new DateTime(2026, 4, 8, 12, 0, 0, DateTimeKind.Utc));

        var model = AdminOperationsCenterComposer.Compose(
            CreateActivationModel(),
            CreateMonitoringSnapshot(),
            CreateClockSnapshot(),
            CreateDriftSnapshot(),
            users,
            bots,
            Array.Empty<ApiCredentialAdminSummary>(),
            GlobalPolicySnapshot.CreateDefault(new DateTime(2026, 4, 8, 12, 0, 0, DateTimeKind.Utc)),
            new BotExecutionPilotOptions { PilotActivationEnabled = true, MaxPilotOrderNotional = "250" },
            null,
            new GlobalExecutionSwitchSnapshot(TradeMasterSwitchState.Disarmed, true, true),
            new GlobalSystemStateSnapshot(GlobalSystemStateKind.Active, "SYSTEM_ACTIVE", null, "AdminPortal", null, false, null, new DateTime(2026, 4, 8, 11, 59, 0, DateTimeKind.Utc), "super-admin", "ip:masked", 1, true),
            true,
            new DateTime(2026, 4, 8, 12, 1, 0, DateTimeKind.Utc));

        Assert.Equal("Blocked", model.PrimaryFlow.Setup.StatusLabel);
        Assert.Equal("Exchange bagli degil", model.PrimaryFlow.Setup.PrimaryMessage);
        var problemUser = Assert.Single(model.UserBotGovernanceCenter.ProblemUsers);
        Assert.Contains("MfaReview", problemUser.Flags);
        var problemBot = Assert.Single(model.UserBotGovernanceCenter.ProblemBots);
        Assert.Contains("BotCooldown", problemBot.Flags);
        Assert.Contains("BotStale", problemBot.Flags);
    }

    [Fact]
    public void Compose_WhenEmergencyStopIsActive_UsesSimpleEmergencyStopStatus()
    {
        var activationModel = CreateActivationModel() with
        {
            CriticalSwitches =
            [
                new AdminActivationSwitchViewModel("trade-master", "TradeMaster", "Disarmed", "critical", "Switch kapali."),
                new AdminActivationSwitchViewModel("kill-switch", "Kill switch", "Ready", "healthy", "Kill switch hazir."),
                new AdminActivationSwitchViewModel("soft-halt", "Soft halt", "Clear", "healthy", "No halt."),
                new AdminActivationSwitchViewModel("emergency-stop", "Emergency stop", "Active", "critical", "Emergency stop aktif.")
            ]
        };

        var model = AdminOperationsCenterComposer.Compose(
            activationModel,
            CreateMonitoringSnapshot(),
            CreateClockSnapshot(),
            CreateDriftSnapshot(),
            AdminUsersPageSnapshot.Empty(new DateTime(2026, 4, 8, 12, 0, 0, DateTimeKind.Utc)),
            AdminBotOperationsPageSnapshot.Empty(new DateTime(2026, 4, 8, 12, 0, 0, DateTimeKind.Utc)),
            [new ApiCredentialAdminSummary(Guid.NewGuid(), "user-1", "Binance", "Primary", false, "fp-****-7890", "Valid", "Env=Testnet;Spot=True;Futures=True;Trade=True", new DateTime(2026, 4, 8, 11, 30, 0, DateTimeKind.Utc), null)],
            GlobalPolicySnapshot.CreateDefault(new DateTime(2026, 4, 8, 12, 0, 0, DateTimeKind.Utc)),
            new BotExecutionPilotOptions { PilotActivationEnabled = true, MaxPilotOrderNotional = "250" },
            null,
            new GlobalExecutionSwitchSnapshot(TradeMasterSwitchState.Disarmed, true, true),
            new GlobalSystemStateSnapshot(GlobalSystemStateKind.FullHalt, "FULL_HALT", null, "AdminPortal", null, false, null, new DateTime(2026, 4, 8, 11, 59, 0, DateTimeKind.Utc), "super-admin", "ip:masked", 1, true),
            true,
            new DateTime(2026, 4, 8, 12, 1, 0, DateTimeKind.Utc));

        Assert.Equal("EmergencyStopActive", model.PrimaryFlow.Activation.StatusLabel);
        var activateAction = Assert.Single(model.PrimaryFlow.Activation.Actions, item => item.Key == "activate");
        var activationActionDebug = string.Join(" | ", model.PrimaryFlow.Activation.Actions.Select(item => $"{item.Key}:{item.IsEnabled}:{item.BlockedReason}"));
        Assert.False(activateAction.IsEnabled, activationActionDebug);
        Assert.Equal("Acil durdurma aktif", activateAction.BlockedReason);
        Assert.Equal("Acil durdurma aktif", model.PrimaryFlow.Activation.PrimaryMessage);
        Assert.Equal("EmergencyStopActive", model.PrimaryFlow.Monitoring.StatusLabel);
    }

    [Fact]
    public void BuildRolloutClosureCenter_WhenEvidenceMissing_FailsClosed()
    {
        var now = new DateTime(2026, 4, 8, 12, 10, 0, DateTimeKind.Utc);
        var model = AdminOperationsCenterComposer.BuildRolloutClosureCenter(
            CreateActivationModel(),
            new GlobalExecutionSwitchSnapshot(TradeMasterSwitchState.Disarmed, true, true),
            new GlobalSystemStateSnapshot(GlobalSystemStateKind.Active, "SYSTEM_ACTIVE", null, "AdminPortal", null, false, null, now.AddMinutes(-1), "super-admin", "ip:masked", 1, true),
            CreateDriftSnapshot(isPersisted: false),
            MonitoringDashboardSnapshot.Empty(now),
            Array.Empty<ApiCredentialAdminSummary>(),
            GlobalPolicySnapshot.CreateDefault(now),
            new BotExecutionPilotOptions
            {
                PilotActivationEnabled = true,
                MaxPilotOrderNotional = "250",
                AllowedUserIds = ["user-1"],
                AllowedBotIds = ["bot-1"],
                AllowedSymbols = ["BTCUSDT"]
            },
            null,
            null,
            Array.Empty<AdminRolloutEvidenceInput>(),
            now);

        Assert.Equal("Blocked", model.StatusLabel);
        Assert.Contains(model.MandatoryGates, item => item.ReasonCode == "BuildEvidenceMissing");
        Assert.Contains(model.BlockingReasons, item => item.ReasonCode == "ContinuityEvidenceMissing");
    }

    [Fact]
    public void BuildRolloutClosureCenter_WhenDemoPilotScopeIsReady_ShowsCurrentDemoAndPilotStages()
    {
        var now = new DateTime(2026, 4, 8, 12, 15, 0, DateTimeKind.Utc);
        var model = AdminOperationsCenterComposer.BuildRolloutClosureCenter(
            CreateActivationModel(),
            new GlobalExecutionSwitchSnapshot(TradeMasterSwitchState.Armed, true, true),
            new GlobalSystemStateSnapshot(GlobalSystemStateKind.Active, "SYSTEM_ACTIVE", null, "AdminPortal", null, false, null, now.AddMinutes(-1), "super-admin", "ip:masked", 1, true),
            CreateDriftSnapshot(),
            CreateHealthyRolloutMonitoringSnapshot(now),
            [new ApiCredentialAdminSummary(Guid.NewGuid(), "user-1", "Binance", "Primary", false, "fp-****-7890", "Valid", "Env=Testnet;Spot=True;Futures=True;Trade=True", now.AddMinutes(-5), null)],
            GlobalPolicySnapshot.CreateDefault(now),
            new BotExecutionPilotOptions
            {
                PilotActivationEnabled = true,
                MaxPilotOrderNotional = "250",
                AllowedUserIds = ["user-1"],
                AllowedBotIds = ["bot-1"],
                AllowedSymbols = ["BTCUSDT"]
            },
            CreateRolloutLogSnapshot(now),
            new LogCenterRetentionSnapshot(true, 45, 45, 90, 180, 180, 250, now.AddMinutes(-10), "Retention ok"),
            CreatePassingRolloutEvidence(now),
            now);

        Assert.Empty(model.BlockingReasons);
        Assert.Contains(model.Stages, item => item.Key == "stage-demo" && item.StatusLabel == "Current");
        Assert.Contains(model.Stages, item => item.Key == "stage-single-pilot" && item.StatusLabel == "Current");
        Assert.Contains(model.GoLiveChecklist, item => item.ReasonCode == "PilotScopeSingle" && item.StatusLabel == "Pass");
    }

    [Fact]
    public void BuildRolloutClosureCenter_WhenLiveApprovalIsMissing_BlocksEnvironmentGate()
    {
        var now = new DateTime(2026, 4, 8, 12, 20, 0, DateTimeKind.Utc);
        var model = AdminOperationsCenterComposer.BuildRolloutClosureCenter(
            CreateActivationModel(),
            new GlobalExecutionSwitchSnapshot(TradeMasterSwitchState.Armed, false, true),
            new GlobalSystemStateSnapshot(GlobalSystemStateKind.Active, "SYSTEM_ACTIVE", null, "AdminPortal", null, false, null, now.AddMinutes(-1), "super-admin", "ip:masked", 1, true),
            CreateDriftSnapshot(),
            CreateHealthyRolloutMonitoringSnapshot(now),
            [new ApiCredentialAdminSummary(Guid.NewGuid(), "user-1", "Binance", "Live Primary", false, "fp-****-9999", "Valid", "Env=Live;Spot=True;Futures=True;Trade=True", now.AddMinutes(-5), null)],
            GlobalPolicySnapshot.CreateDefault(now),
            new BotExecutionPilotOptions
            {
                PilotActivationEnabled = true,
                MaxPilotOrderNotional = "250",
                AllowedUserIds = ["user-1"],
                AllowedBotIds = ["bot-1"],
                AllowedSymbols = ["BTCUSDT"]
            },
            CreateRolloutLogSnapshot(now),
            new LogCenterRetentionSnapshot(true, 45, 45, 90, 180, 180, 250, now.AddMinutes(-10), "Retention ok"),
            CreatePassingRolloutEvidence(now),
            now);

        Assert.Equal("Blocked", model.StatusLabel);
        Assert.Contains(model.GoLiveChecklist, item => item.ReasonCode == "LiveModeApprovalMissing" && item.StatusLabel == "Blocked");
        Assert.Contains(model.BlockingReasons, item => item.ReasonCode == "LiveModeApprovalMissing");
    }
    private static AdminActivationControlCenterViewModel CreateActivationModel() => new(
        false,
        true,
        "Aktif edilebilir",
        "warning",
        "Checklist gecerli.",
        "Demo",
        "Demo execution yolu acik.",
        new AdminActivationDecisionViewModel("Allow", "warning", "ActivationReady", "Hazir", "ActivationControlCenter.Readiness", "2026-04-08 12:00 UTC", "Mode=Demo | TradeMaster=Disarmed"),
        [new AdminActivationSwitchViewModel("trade-master", "TradeMaster", "Disarmed", "critical", "Switch kapali."), new AdminActivationSwitchViewModel("kill-switch", "Kill switch", "Ready", "healthy", "Kill switch hazir."), new AdminActivationSwitchViewModel("soft-halt", "Soft halt", "Clear", "healthy", "No halt."), new AdminActivationSwitchViewModel("emergency-stop", "Emergency stop", "Clear", "healthy", "No stop.")],
        [new AdminActivationReadinessItemViewModel("config", "Config", "Pass", "pass", "healthy", "Readable", "Config ok.", "Service")]);

    private static MonitoringDashboardSnapshot CreateHealthyRolloutMonitoringSnapshot(DateTime now)
    {
        var handoff = MarketScannerHandoffSnapshot.Empty() with
        {
            CompletedAtUtc = now.AddMinutes(-1),
            DecisionReasonCode = "DemoFillSimulated",
            DecisionSummary = "Pilot smoke completed.",
            MarketDataStaleReason = null,
            ContinuityState = "Recovered",
            ContinuityGapCount = 0,
            ContinuityRecoveredAtUtc = now.AddMinutes(-1)
        };

        return MonitoringDashboardSnapshot.Empty(now) with
        {
            MarketScanner = MarketScannerDashboardSnapshot.Empty() with
            {
                LatestHandoff = handoff,
                LastSuccessfulHandoff = handoff,
                LastBlockedHandoff = MarketScannerHandoffSnapshot.Empty()
            }
        };
    }

    private static LogCenterPageSnapshot CreateRolloutLogSnapshot(DateTime now)
    {
        return new LogCenterPageSnapshot(
            new LogCenterQueryRequest(null, null, null, null, null, null, null, null, null, 40),
            new LogCenterSummarySnapshot(1, 1, 0, 1, 0, 0, 0, 0, 1, now),
            new LogCenterRetentionSnapshot(true, 45, 45, 90, 180, 180, 250, now.AddMinutes(-5), "Retention ok"),
            [new LogCenterEntrySnapshot("DecisionTrace", "DEC-1", "Allow", "warning", null, "corr-rollout-1", "DEC-1", "EXE-1", "INC-1", "APR-1", "super-admin", "BTCUSDT", "Activation ready", "Rollout decision visible.", "ActivationControlCenter", now, ["rollout"], null, "Decision", "ActivationReady", "Ready", now)],
            false,
            null);
    }

    private static IReadOnlyCollection<AdminRolloutEvidenceInput> CreatePassingRolloutEvidence(DateTime now)
    {
        return
        [
            new AdminRolloutEvidenceInput("build-clean", "Build temiz", true, "BuildClean", "Build succeeded.", "dotnet build CoinBot.sln", now),
            new AdminRolloutEvidenceInput("unit-tests-clean", "Unit test temiz", true, "UnitTestsClean", "Unit tests succeeded.", "dotnet test CoinBot.UnitTests", now),
            new AdminRolloutEvidenceInput("ui-smoke-clean", "UI smoke temiz", true, "UiSmokeClean", "Settings smoke rollout closure surface rendered.", "SettingsBrowserSmoke", now),
            new AdminRolloutEvidenceInput("pilot-lifecycle-clean", "Pilot lifecycle smoke temiz", true, "PilotLifecycleClean", "Pilot lifecycle smoke cleaned scoped exposure.", "PilotLifecycleRuntimeSmoke", now),
            new AdminRolloutEvidenceInput("kill-switch-tested", "Kill switch test edildi", true, "KillSwitchTested", "Ui live smoke verified TradeMaster disarmed reject.", "UiLiveBrowserSmoke", now)
        ];
    }
    private static MonitoringDashboardSnapshot CreateMonitoringSnapshot()
    {
        return new MonitoringDashboardSnapshot(
            Array.Empty<HealthSnapshot>(),
            [new WorkerHeartbeat("worker-1", "Bot Worker", MonitoringHealthState.Healthy, MonitoringFreshnessTier.Hot, CircuitBreakerStateCode.Closed, new DateTime(2026, 4, 8, 11, 59, 50, DateTimeKind.Utc), new DateTime(2026, 4, 8, 11, 59, 55, DateTimeKind.Utc), 0)],
            new DateTime(2026, 4, 8, 12, 0, 0, DateTimeKind.Utc));
    }

    private static BinanceTimeSyncSnapshot CreateClockSnapshot() => new(
        new DateTime(2026, 4, 8, 12, 0, 0, DateTimeKind.Utc),
        new DateTime(2026, 4, 8, 12, 0, 0, DateTimeKind.Utc),
        0,
        12,
        new DateTime(2026, 4, 8, 12, 0, 0, DateTimeKind.Utc),
        "Synchronized",
        null);

    private static DegradedModeSnapshot CreateDriftSnapshot(bool isPersisted = true) => new(
        DegradedModeStateCode.Normal,
        DegradedModeReasonCode.None,
        false,
        false,
        new DateTime(2026, 4, 8, 11, 59, 0, DateTimeKind.Utc),
        new DateTime(2026, 4, 8, 11, 59, 5, DateTimeKind.Utc),
        500,
        8,
        new DateTime(2026, 4, 8, 11, 59, 10, DateTimeKind.Utc),
        isPersisted);
}











