using System.Globalization;
using System.Security.Claims;
using CoinBot.Application.Abstractions.Dashboard;
using CoinBot.Application.Abstractions.Settings;
using CoinBot.Web.ViewModels.AiRobot;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CoinBot.Web.Controllers;

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
            snapshot.HypotheticalBlockReasons.Select(item => new AiRobotBucketViewModel(item.Label, item.Count)).ToList()));
    }

    private static AiRobotSummaryViewModel BuildSummary(UserDashboardLiveSnapshot snapshot)
    {
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
            snapshot.AiHistory.Count == 0 ? "Henüz gerçek AI shadow kaydı yok." : string.Empty);
    }

    private static List<AiRobotDecisionViewModel> BuildDecisions(
        UserDashboardLiveSnapshot snapshot,
        TimeZoneInfo displayTimeZoneInfo)
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
                ResolveTone(item),
                item.AiIsFallback))
            .ToList();
    }

    private static string BuildRegimeSummary(UserDashboardAiHistoryRowSnapshot snapshot)
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(snapshot.PrimaryRegime))
        {
            parts.Add($"Regime={snapshot.PrimaryRegime}");
        }

        if (!string.IsNullOrWhiteSpace(snapshot.MomentumBias))
        {
            parts.Add($"Momentum={snapshot.MomentumBias}");
        }

        if (!string.IsNullOrWhiteSpace(snapshot.VolatilityState))
        {
            parts.Add($"Volatility={snapshot.VolatilityState}");
        }

        if (!string.IsNullOrWhiteSpace(snapshot.TopSignalHints))
        {
            parts.Add(snapshot.TopSignalHints!);
        }

        return parts.Count == 0
            ? "Feature context henüz zenginleşmedi."
            : string.Join(" • ", parts);
    }

    private static string ResolveTone(UserDashboardAiHistoryRowSnapshot snapshot)
    {
        if (snapshot.AiIsFallback)
        {
            return "warning";
        }

        if (snapshot.RiskVetoPresent || snapshot.PilotSafetyBlocked || string.Equals(snapshot.FinalAction, "NoSubmit", StringComparison.Ordinal))
        {
            return "danger";
        }

        return snapshot.AiDirection switch
        {
            "Long" => "success",
            "Short" => "danger",
            _ => "neutral"
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

    private static string FormatTimestamp(DateTime timestampUtc, TimeZoneInfo timeZoneInfo)
    {
        var normalizedTimestamp = timestampUtc.Kind == DateTimeKind.Utc
            ? timestampUtc
            : DateTime.SpecifyKind(timestampUtc, DateTimeKind.Utc);
        var localTimestamp = TimeZoneInfo.ConvertTimeFromUtc(normalizedTimestamp, timeZoneInfo);
        return $"{localTimestamp:yyyy-MM-dd HH:mm:ss} {timeZoneInfo.StandardName}";
    }
}
