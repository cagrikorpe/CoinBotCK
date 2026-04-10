using System.Globalization;
using System.Security.Claims;
using CoinBot.Application.Abstractions.Dashboard;
using CoinBot.Application.Abstractions.Settings;
using CoinBot.Domain.Enums;
using CoinBot.Web.ViewModels.AiRobot;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CoinBot.Web.Controllers;

[RedirectSuperAdminToAdminOverview]
[Authorize]
public sealed class AiRobotController(
    IUserDashboardLiveReadModelService userDashboardLiveReadModelService,
    IUserSettingsService userSettingsService) : Controller
{
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var userId = HttpContext?.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Challenge();
        }

        var settingsSnapshot = await userSettingsService.GetAsync(userId, cancellationToken);
        var displayTimeZoneInfo = ResolveTimeZone(settingsSnapshot?.PreferredTimeZoneId);
        var displayTimeZoneJavaScriptId = settingsSnapshot?.PreferredTimeZoneJavaScriptId ?? "UTC";
        var snapshot = await userDashboardLiveReadModelService.GetSnapshotAsync(userId, cancellationToken);

        return View(new AiRobotViewModel(
            displayTimeZoneInfo.Id,
            displayTimeZoneJavaScriptId,
            displayTimeZoneInfo.DisplayName,
            BuildSummary(snapshot),
            BuildDecisions(snapshot, displayTimeZoneInfo),
            snapshot.NoSubmitReasons.Select(item => new AiRobotBucketViewModel(item.Label, item.Count)).ToList(),
            snapshot.HypotheticalBlockReasons.Select(item => new AiRobotBucketViewModel(item.Label, item.Count)).ToList(),
            (snapshot.OutcomeStates ?? []).Select(item => new AiRobotBucketViewModel(item.Label, item.Count)).ToList(),
            (snapshot.FutureDataAvailabilityBuckets ?? []).Select(item => new AiRobotBucketViewModel(item.Label, item.Count)).ToList(),
            (snapshot.OutcomeConfidenceBuckets ?? []).Select(item => new AiRobotConfidenceBucketViewModel(
                item.Label,
                item.TotalCount,
                item.ScoredCount,
                item.SuccessCount,
                item.FalsePositiveCount,
                item.FalseNeutralCount,
                item.OvertradingCount,
                FormatSignedDecimal(item.AverageOutcomeScore))).ToList()));
    }

    private static AiRobotSummaryViewModel BuildSummary(UserDashboardLiveSnapshot snapshot)
    {
        var outcomeSummary = snapshot.AiOutcomeSummary ?? new UserDashboardAiOutcomeSummarySnapshot(
            "+1 bar close-to-close",
            0,
            0,
            0,
            0,
            0m,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0);

        return new AiRobotSummaryViewModel(
            snapshot.AiSummary.TotalCount,
            snapshot.AiSummary.LongCount,
            snapshot.AiSummary.ShortCount,
            snapshot.AiSummary.NeutralCount,
            snapshot.AiSummary.FallbackCount,
            snapshot.AiSummary.AgreementCount,
            snapshot.AiSummary.DisagreementCount,
            $"{snapshot.AiSummary.AverageConfidence:P0}",
            snapshot.LatestNoTrade.Label,
            snapshot.LatestNoTrade.Summary,
            snapshot.AiHistory.Count == 0 ? "Henüz gerçek AI shadow kaydı yok." : string.Empty,
            outcomeSummary.HorizonLabel,
            $"{outcomeSummary.ScoredCount}/{outcomeSummary.TotalDecisionCount}",
            FormatSignedDecimal(outcomeSummary.AverageOutcomeScore),
            $"+ {outcomeSummary.PositiveOutcomeCount} / - {outcomeSummary.NegativeOutcomeCount} / flat {outcomeSummary.NeutralOutcomeCount}",
            $"False+ {outcomeSummary.FalsePositiveCount} · FalseNeutral {outcomeSummary.FalseNeutralCount} · Overtrade {outcomeSummary.OvertradingCount} · Suppression {outcomeSummary.SuppressionAlignedCount}/{outcomeSummary.SuppressionMissedCount}",
            snapshot.Control.TradeMasterLabel,
            snapshot.Control.TradingModeLabel,
            snapshot.Control.PilotActivationLabel,
            snapshot.Control.MarketDataLabel,
            snapshot.Control.PrivatePlaneLabel,
            snapshot.LatestReject.Label,
            string.IsNullOrWhiteSpace(snapshot.LatestReject.Code)
                ? snapshot.LatestReject.Summary
                : $"{snapshot.LatestReject.Code} · {snapshot.LatestReject.Summary}");
    }

    private static List<AiRobotDecisionViewModel> BuildDecisions(UserDashboardLiveSnapshot snapshot, TimeZoneInfo displayTimeZoneInfo)
    {
        return snapshot.AiHistory
            .OrderByDescending(item => item.EvaluatedAtUtc)
            .Take(20)
            .Select(item => new AiRobotDecisionViewModel(
                FormatTimestamp(item.EvaluatedAtUtc, displayTimeZoneInfo),
                item.Symbol,
                item.Timeframe,
                item.StrategyDirection,
                item.StrategyConfidenceScore.HasValue ? $"{item.StrategyConfidenceScore.Value}%" : "-",
                item.AiDirection,
                $"{item.AiConfidence:P0}",
                item.AgreementState,
                item.FinalAction,
                item.NoSubmitReason,
                item.HypotheticalBlockReason,
                string.IsNullOrWhiteSpace(item.AiProviderModel) ? item.AiProviderName : $"{item.AiProviderName} / {item.AiProviderModel}",
                item.AiReasonSummary,
                item.FeatureSnapshotId.HasValue ? item.FeatureSnapshotId.Value.ToString("N")[..8].ToUpperInvariant() : "-",
                item.FeatureSummary ?? "Feature snapshot summary yok.",
                BuildRegimeSummary(item),
                item.RiskVetoPresent ? $"{item.RiskVetoReason ?? "RiskVeto"} · {item.RiskVetoSummary ?? "Risk summary yok."}" : "Risk veto yok.",
                item.PilotSafetyBlocked ? $"{item.PilotSafetyReason ?? "PilotBlock"} · {item.PilotSafetySummary ?? "Pilot summary yok."}" : "Pilot safety block yok.",
                ResolveOutcomeLabel(item),
                ResolveOutcomeDetail(item),
                ResolveTone(item),
                item.AiIsFallback,
                BuildStrategySummary(item),
                BuildOverlaySummary(item),
                BuildFinalReasonSummary(item),
                item.TopSignalHints ?? "Top feature hints yok."))
            .ToList();
    }

    private static string BuildStrategySummary(UserDashboardAiHistoryRowSnapshot snapshot)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(snapshot.StrategyDecisionCode)) parts.Add(snapshot.StrategyDecisionCode!);
        if (!string.IsNullOrWhiteSpace(snapshot.StrategyDecisionOutcome)) parts.Add(snapshot.StrategyDecisionOutcome!);
        if (!string.IsNullOrWhiteSpace(snapshot.StrategySummary)) parts.Add(snapshot.StrategySummary!);
        return parts.Count == 0 ? "Strategy summary yok." : string.Join(" · ", parts);
    }

    private static string BuildOverlaySummary(UserDashboardAiHistoryRowSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(snapshot.AiOverlayDisposition))
        {
            return "AI overlay uygulanmadı.";
        }

        return snapshot.AiOverlayBoostPoints == 0
            ? $"Overlay={snapshot.AiOverlayDisposition}"
            : $"Overlay={snapshot.AiOverlayDisposition} · Boost={snapshot.AiOverlayBoostPoints}";
    }

    private static string BuildFinalReasonSummary(UserDashboardAiHistoryRowSnapshot snapshot)
    {
        var parts = new List<string> { $"Final={snapshot.FinalAction}" };
        if (!string.IsNullOrWhiteSpace(snapshot.NoSubmitReason)) parts.Add($"NoSubmit={snapshot.NoSubmitReason}");
        if (!string.IsNullOrWhiteSpace(snapshot.HypotheticalBlockReason)) parts.Add($"HypBlock={snapshot.HypotheticalBlockReason}");
        if (snapshot.RiskVetoPresent && !string.IsNullOrWhiteSpace(snapshot.RiskVetoReason)) parts.Add($"Risk={snapshot.RiskVetoReason}");
        if (snapshot.PilotSafetyBlocked && !string.IsNullOrWhiteSpace(snapshot.PilotSafetyReason)) parts.Add($"Safety={snapshot.PilotSafetyReason}");
        if (snapshot.AiIsFallback && !string.IsNullOrWhiteSpace(snapshot.AiFallbackReason)) parts.Add($"Fallback={snapshot.AiFallbackReason}");
        return string.Join(" · ", parts);
    }

    private static string BuildRegimeSummary(UserDashboardAiHistoryRowSnapshot snapshot)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(snapshot.PrimaryRegime)) parts.Add($"Regime={snapshot.PrimaryRegime}");
        if (!string.IsNullOrWhiteSpace(snapshot.MomentumBias)) parts.Add($"Momentum={snapshot.MomentumBias}");
        if (!string.IsNullOrWhiteSpace(snapshot.VolatilityState)) parts.Add($"Volatility={snapshot.VolatilityState}");
        return parts.Count == 0 ? "Feature context henüz zenginleşmedi." : string.Join(" • ", parts);
    }

    private static string ResolveOutcomeLabel(UserDashboardAiHistoryRowSnapshot snapshot)
    {
        if (!snapshot.OutcomeState.HasValue)
        {
            return "Outcome yok";
        }

        var horizonLabel = snapshot.OutcomeHorizonValue.HasValue ? $"+{snapshot.OutcomeHorizonValue.Value} bar" : "+?";
        return $"{snapshot.OutcomeState.Value} · {horizonLabel}";
    }

    private static string ResolveOutcomeDetail(UserDashboardAiHistoryRowSnapshot snapshot)
    {
        if (!snapshot.OutcomeState.HasValue)
        {
            return "Henüz score edilmedi.";
        }

        if (snapshot.OutcomeState != AiShadowOutcomeState.Scored)
        {
            return $"Data={snapshot.FutureDataAvailability?.ToString() ?? "Unknown"}; Bucket={snapshot.OutcomeConfidenceBucket}";
        }

        var flags = new List<string>();
        if (snapshot.FalsePositive) flags.Add("FalsePositive");
        if (snapshot.FalseNeutral) flags.Add("FalseNeutral");
        if (snapshot.Overtrading) flags.Add("Overtrading");
        if (snapshot.SuppressionCandidate) flags.Add(snapshot.SuppressionAligned ? "SuppressionAligned" : "SuppressionMissed");
        var flagText = flags.Count == 0 ? "Flags=none" : $"Flags={string.Join(',', flags)}";
        return $"Score={FormatSignedDecimal(snapshot.OutcomeScore ?? 0m)}; Move={snapshot.RealizedDirectionality ?? "Unknown"}; Bucket={snapshot.OutcomeConfidenceBucket}; {flagText}";
    }

    private static string ResolveTone(UserDashboardAiHistoryRowSnapshot snapshot)
    {
        if (snapshot.AiIsFallback) return "warning";
        if (snapshot.OutcomeState == AiShadowOutcomeState.Scored && (snapshot.OutcomeScore ?? 0m) > 0m) return "success";
        if (snapshot.OutcomeState == AiShadowOutcomeState.Scored && (snapshot.OutcomeScore ?? 0m) < 0m) return "danger";
        if (snapshot.RiskVetoPresent || snapshot.PilotSafetyBlocked || string.Equals(snapshot.FinalAction, "NoSubmit", StringComparison.Ordinal)) return "danger";

        return snapshot.AiDirection switch
        {
            "Long" => "success",
            "Short" => "danger",
            _ => "neutral"
        };
    }

    private static string FormatSignedDecimal(decimal value)
    {
        return value > 0m
            ? $"+{value.ToString("0.000", CultureInfo.InvariantCulture)}"
            : value.ToString("0.000", CultureInfo.InvariantCulture);
    }

    private static TimeZoneInfo ResolveTimeZone(string? timeZoneId)
    {
        if (!string.IsNullOrWhiteSpace(timeZoneId))
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId.Trim()); }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }

        return TimeZoneInfo.Utc;
    }

    private static string FormatTimestamp(DateTime timestampUtc, TimeZoneInfo timeZoneInfo)
    {
        var normalizedTimestamp = timestampUtc.Kind == DateTimeKind.Utc ? timestampUtc : DateTime.SpecifyKind(timestampUtc, DateTimeKind.Utc);
        var localTimestamp = TimeZoneInfo.ConvertTimeFromUtc(normalizedTimestamp, timeZoneInfo);
        return $"{localTimestamp:yyyy-MM-dd HH:mm:ss} {timeZoneInfo.StandardName}";
    }
}

