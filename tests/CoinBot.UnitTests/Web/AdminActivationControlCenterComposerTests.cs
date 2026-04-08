using CoinBot.Application.Abstractions.Administration;
using CoinBot.Application.Abstractions.Execution;
using CoinBot.Application.Abstractions.Exchange;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Jobs;
using CoinBot.Web.ViewModels.Admin;

namespace CoinBot.UnitTests.Web;

public sealed class AdminActivationControlCenterComposerTests
{
    [Fact]
    public void Compose_WhenReadinessPass_ReturnsActivatableAllowSummary()
    {
        var model = AdminActivationControlCenterComposer.Compose(
            CreateExecutionSnapshot(),
            CreateSystemStateSnapshot(),
            CreateTimeSyncSnapshot(),
            CreateDriftGuardSnapshot(),
            CreatePilotOptions(),
            "250",
            "healthy",
            new DateTime(2026, 4, 8, 10, 0, 0, DateTimeKind.Utc));

        Assert.True(model.IsActivatable);
        Assert.False(model.IsCurrentlyActive);
        Assert.Equal("ActivationReady", model.LastDecision.Code);
        Assert.Equal("Allow", model.LastDecision.TypeLabel);
        Assert.Equal(7, model.ReadinessChecklist.Count);
        Assert.Empty(model.BlockingItems);
    }

    [Fact]
    public void Compose_WhenStateSnapshotIsMissing_FailsClosedWithUnknownDecision()
    {
        var model = AdminActivationControlCenterComposer.Compose(
            CreateExecutionSnapshot(isPersisted: false),
            CreateSystemStateSnapshot(isPersisted: false),
            CreateTimeSyncSnapshot(),
            CreateDriftGuardSnapshot(),
            CreatePilotOptions(),
            "250",
            "healthy",
            new DateTime(2026, 4, 8, 10, 5, 0, DateTimeKind.Utc));

        Assert.False(model.IsActivatable);
        Assert.Equal("ActivationStateUnavailable", model.LastDecision.Code);
        Assert.Contains(model.ReadinessChecklist, item => item.ReasonCode == "ActivationStateUnavailable" && item.StatusCode == "unknown");
    }

    [Fact]
    public void Compose_WhenEmergencyStopIsActive_BlocksActivation()
    {
        var model = AdminActivationControlCenterComposer.Compose(
            CreateExecutionSnapshot(),
            CreateSystemStateSnapshot(GlobalSystemStateKind.FullHalt, "FULL_HALT", isPersisted: true),
            CreateTimeSyncSnapshot(),
            CreateDriftGuardSnapshot(),
            CreatePilotOptions(),
            "250",
            "healthy",
            new DateTime(2026, 4, 8, 10, 10, 0, DateTimeKind.Utc));

        Assert.False(model.IsActivatable);
        Assert.Equal("GlobalSystemFullHalt", model.LastDecision.Code);
        Assert.Contains(model.CriticalSwitches, item => item.Key == "emergency-stop" && item.Value == "Active");
    }

    [Fact]
    public void Compose_WhenLiveModeApprovalIsMissing_BlocksActivation()
    {
        var model = AdminActivationControlCenterComposer.Compose(
            CreateExecutionSnapshot(demoModeEnabled: false, liveModeApprovedAtUtc: null),
            CreateSystemStateSnapshot(),
            CreateTimeSyncSnapshot(),
            CreateDriftGuardSnapshot(),
            CreatePilotOptions(),
            "250",
            "healthy",
            new DateTime(2026, 4, 8, 10, 15, 0, DateTimeKind.Utc));

        Assert.False(model.IsActivatable);
        Assert.Equal("LiveModeApprovalMissing", model.LastDecision.Code);
        Assert.Contains(model.ReadinessChecklist, item => item.ReasonCode == "LiveModeApprovalMissing" && item.StatusCode == "fail");
    }

    [Fact]
    public void Compose_WhenReadinessGateFails_BlocksActivation()
    {
        var model = AdminActivationControlCenterComposer.Compose(
            CreateExecutionSnapshot(),
            CreateSystemStateSnapshot(),
            CreateTimeSyncSnapshot(),
            CreateDriftGuardSnapshot(
                DegradedModeStateCode.Stopped,
                DegradedModeReasonCode.ClockDriftExceeded,
                signalFlowBlocked: true,
                executionFlowBlocked: true,
                isPersisted: true),
            CreatePilotOptions(),
            "250",
            "healthy",
            new DateTime(2026, 4, 8, 10, 20, 0, DateTimeKind.Utc));

        Assert.False(model.IsActivatable);
        Assert.Equal("ClockDriftExceeded", model.LastDecision.Code);
        Assert.Contains(model.ReadinessChecklist, item => item.ReasonCode == "ClockDriftExceeded" && item.StatusCode == "fail");
    }

    private static GlobalExecutionSwitchSnapshot CreateExecutionSnapshot(
        TradeMasterSwitchState tradeMasterState = TradeMasterSwitchState.Disarmed,
        bool demoModeEnabled = true,
        bool isPersisted = true,
        DateTime? liveModeApprovedAtUtc = null)
    {
        return new GlobalExecutionSwitchSnapshot(
            tradeMasterState,
            demoModeEnabled,
            isPersisted,
            liveModeApprovedAtUtc);
    }

    private static GlobalSystemStateSnapshot CreateSystemStateSnapshot(
        GlobalSystemStateKind state = GlobalSystemStateKind.Active,
        string reasonCode = "SYSTEM_ACTIVE",
        bool isPersisted = true)
    {
        return new GlobalSystemStateSnapshot(
            state,
            reasonCode,
            Message: null,
            Source: "AdminPortal.Settings",
            CorrelationId: "corr-activation-composer",
            IsManualOverride: false,
            ExpiresAtUtc: null,
            UpdatedAtUtc: new DateTime(2026, 4, 8, 9, 55, 0, DateTimeKind.Utc),
            UpdatedByUserId: "super-admin",
            UpdatedFromIp: "ip:masked",
            Version: 1,
            IsPersisted: isPersisted);
    }

    private static BinanceTimeSyncSnapshot CreateTimeSyncSnapshot(
        string statusCode = "Synchronized",
        string? failureReason = null,
        bool hasExchangeTime = true)
    {
        var localTime = new DateTime(2026, 4, 8, 9, 59, 0, DateTimeKind.Utc);
        return new BinanceTimeSyncSnapshot(
            localTime,
            hasExchangeTime ? localTime.AddMilliseconds(5) : null,
            OffsetMilliseconds: 5,
            RoundTripMilliseconds: 18,
            LastSynchronizedAtUtc: hasExchangeTime ? localTime : null,
            StatusCode: statusCode,
            FailureReason: failureReason);
    }

    private static DegradedModeSnapshot CreateDriftGuardSnapshot(
        DegradedModeStateCode stateCode = DegradedModeStateCode.Normal,
        DegradedModeReasonCode reasonCode = DegradedModeReasonCode.None,
        bool signalFlowBlocked = false,
        bool executionFlowBlocked = false,
        bool isPersisted = true)
    {
        return new DegradedModeSnapshot(
            stateCode,
            reasonCode,
            signalFlowBlocked,
            executionFlowBlocked,
            LatestDataTimestampAtUtc: new DateTime(2026, 4, 8, 9, 58, 0, DateTimeKind.Utc),
            LatestHeartbeatReceivedAtUtc: new DateTime(2026, 4, 8, 9, 58, 5, DateTimeKind.Utc),
            LatestDataAgeMilliseconds: 500,
            LatestClockDriftMilliseconds: 8,
            LastStateChangedAtUtc: new DateTime(2026, 4, 8, 9, 58, 10, DateTimeKind.Utc),
            IsPersisted: isPersisted);
    }

    private static BotExecutionPilotOptions CreatePilotOptions()
    {
        return new BotExecutionPilotOptions
        {
            PilotActivationEnabled = true,
            MaxPilotOrderNotional = "250"
        };
    }
}
