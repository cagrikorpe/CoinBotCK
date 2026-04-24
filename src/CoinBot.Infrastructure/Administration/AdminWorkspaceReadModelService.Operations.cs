using System.Text.Json;
using CoinBot.Application.Abstractions.Administration;
using CoinBot.Application.Abstractions.Ai;
using CoinBot.Application.Abstractions.Strategies;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;

namespace CoinBot.Infrastructure.Administration;

public sealed partial class AdminWorkspaceReadModelService
{
    public async Task<AdminBotOperationsPageSnapshot> GetBotOperationsAsync(
        string? query = null,
        string? status = null,
        string? mode = null,
        CancellationToken cancellationToken = default)
    {
        var now = UtcNow;
        var normalizedQuery = NormalizeOptional(query);
        var normalizedStatus = NormalizeOptional(status);
        var normalizedMode = NormalizeOptional(mode);

        var bots = await dbContext.TradingBots
            .AsNoTracking()
            .Where(bot => !bot.IsDeleted)
            .OrderByDescending(bot => bot.UpdatedDate)
            .Take(200)
            .ToListAsync(cancellationToken);
        var users = await dbContext.Users
            .AsNoTracking()
            .ToDictionaryAsync(user => user.Id, user => user, cancellationToken);
        var latestOrderFailures = await LoadLatestOrderFailuresAsync(cancellationToken);

        var rows = bots.Select(bot =>
        {
            users.TryGetValue(bot.OwnerUserId, out var owner);
            var ownerDisplay = owner is null
                ? bot.OwnerUserId
                : string.IsNullOrWhiteSpace(owner.FullName) ? owner.UserName ?? owner.Email ?? owner.Id : owner.FullName;
            var effectiveMode = bot.TradingModeOverride ?? owner?.TradingModeOverride ?? ExecutionEnvironment.Demo;
            var lastFailure = latestOrderFailures.TryGetValue(bot.Id, out var failure)
                ? failure
                : "No failure";

            return new AdminBotOperationSnapshot(
                bot.Id.ToString(),
                bot.Name,
                bot.OwnerUserId,
                ownerDisplay,
                bot.IsEnabled ? "Aktif" : "Durduruldu",
                bot.IsEnabled ? "healthy" : "neutral",
                BuildTradingModeLabel(effectiveMode),
                BuildTradingModeTone(effectiveMode),
                bot.StrategyKey,
                bot.OpenPositionCount > 0 ? "Exposure var" : "Exposure yok",
                bot.OpenPositionCount > 0 ? "warning" : "neutral",
                bot.IsEnabled ? "Çalışıyor" : "Durduruldu",
                lastFailure,
                bot.OpenOrderCount,
                bot.OpenPositionCount);
        }).ToArray();

        rows = rows
            .Where(row => string.IsNullOrWhiteSpace(normalizedQuery) ||
                          row.Name.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase) ||
                          row.OwnerDisplayName.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase) ||
                          row.StrategyKey.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase))
            .Where(row => string.IsNullOrWhiteSpace(normalizedStatus) ||
                          row.StatusLabel.Contains(normalizedStatus, StringComparison.OrdinalIgnoreCase))
            .Where(row => string.IsNullOrWhiteSpace(normalizedMode) ||
                          row.ModeLabel.Contains(normalizedMode, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var summaryTiles = new[]
        {
            new AdminStatTileSnapshot("Toplam bot", bots.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), "User-owned bots", "info"),
            new AdminStatTileSnapshot("Aktif bot", rows.Count(row => row.StatusTone == "healthy").ToString(System.Globalization.CultureInfo.InvariantCulture), "Running / enabled", "healthy"),
            new AdminStatTileSnapshot("Pozisyonlu bot", rows.Count(row => row.OpenPositionCount > 0).ToString(System.Globalization.CultureInfo.InvariantCulture), "Open exposure", "warning"),
            new AdminStatTileSnapshot("Hata botu", rows.Count(row => !string.Equals(row.LastError, "No failure", StringComparison.OrdinalIgnoreCase)).ToString(System.Globalization.CultureInfo.InvariantCulture), "Execution failures", "critical")
        };

        return new AdminBotOperationsPageSnapshot(normalizedQuery, normalizedStatus, normalizedMode, summaryTiles, rows, now);
    }

    public async Task<AdminStrategyAiMonitoringPageSnapshot> GetStrategyAiMonitoringAsync(
        string? query = null,
        CancellationToken cancellationToken = default)
    {
        var now = UtcNow;
        var normalizedQuery = NormalizeOptional(query);

        var strategies = await dbContext.TradingStrategies
            .AsNoTracking()
            .Where(strategy => !strategy.IsDeleted)
            .OrderBy(strategy => strategy.DisplayName)
            .ToListAsync(cancellationToken);
        var signals = await dbContext.TradingStrategySignals
            .AsNoTracking()
            .Where(signal => !signal.IsDeleted)
            .ToListAsync(cancellationToken);
        var vetoes = await dbContext.TradingStrategySignalVetoes
            .AsNoTracking()
            .Where(veto => !veto.IsDeleted)
            .ToListAsync(cancellationToken);
        var versions = await dbContext.TradingStrategyVersions
            .AsNoTracking()
            .Where(version => !version.IsDeleted)
            .ToListAsync(cancellationToken);
        var recentShadowDecisions = await dbContext.AiShadowDecisions
            .AsNoTracking()
            .Where(entity => !entity.IsDeleted)
            .OrderByDescending(entity => entity.EvaluatedAtUtc)
            .Take(200)
            .ToListAsync(cancellationToken);
        var recentShadowDecisionIds = recentShadowDecisions.Select(entity => entity.Id).ToArray();
        var recentShadowOutcomes = recentShadowDecisionIds.Length == 0
            ? new List<AiShadowDecisionOutcome>()
            : await dbContext.AiShadowDecisionOutcomes
                .AsNoTracking()
                .Where(entity =>
                    recentShadowDecisionIds.Contains(entity.AiShadowDecisionId) &&
                    entity.HorizonKind == AiShadowOutcomeDefaults.OfficialHorizonKind &&
                    entity.HorizonValue == AiShadowOutcomeDefaults.OfficialHorizonValue &&
                    !entity.IsDeleted)
                .ToListAsync(cancellationToken);
        var templateSnapshots = await strategyTemplateCatalogService.ListAllAsync(cancellationToken);
        var templates = templateSnapshots
            .Select(template => new AdminStrategyTemplateSnapshot(
                template.TemplateKey,
                template.TemplateName,
                template.Category,
                template.Validation.StatusCode,
                template.Validation.Summary,
                template.SchemaVersion,
                template.Description,
                template.ActiveRevisionNumber,
                template.LatestRevisionNumber,
                template.PublishedRevisionNumber,
                template.TemplateSource,
                template.IsActive ? "Active" : "Archived",
                BuildTemplateLineageLabel(template)))
            .ToArray();

        var versionLookup = versions
            .GroupBy(version => version.TradingStrategyId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyCollection<TradingStrategyVersion>)group.ToArray());

        var allUsageRows = strategies.Select(strategy =>
        {
            var strategySignals = signals.Where(signal => signal.TradingStrategyId == strategy.Id).ToArray();
            var strategyVetoes = vetoes.Where(veto => veto.TradingStrategyId == strategy.Id).ToArray();
            var strategyVersions = versionLookup.TryGetValue(strategy.Id, out var scopedVersions)
                ? scopedVersions
                : Array.Empty<TradingStrategyVersion>();
            var latestSignal = strategySignals.OrderByDescending(signal => signal.GeneratedAtUtc).FirstOrDefault();
            var latestVeto = strategyVetoes.OrderByDescending(veto => veto.EvaluatedAtUtc).FirstOrDefault();
            var runtimeVersion = ResolveRuntimeStrategyVersion(strategy, strategyVersions);
            var latestVersion = strategyVersions
                .OrderByDescending(version => version.VersionNumber)
                .FirstOrDefault();
            var definitionSummary = BuildStrategyDefinitionSummary(runtimeVersion ?? latestVersion);
            var explainabilitySummary = BuildStrategyExplainabilitySummary(latestSignal, latestVeto);
            var signalCount = strategySignals.Length;
            var vetoCount = strategyVetoes.Length;
            var vetoRate = signalCount == 0 ? 0m : Math.Round((decimal)vetoCount / signalCount, 4);

            return new AdminStrategyUsageSnapshot(
                strategy.StrategyKey,
                strategy.DisplayName,
                BuildStrategyHealthLabel(strategy, signalCount, vetoCount),
                BuildStrategyHealthTone(strategy, signalCount, vetoCount),
                signalCount,
                vetoCount,
                vetoRate,
                latestSignal is null ? "-" : latestSignal.SignalType.ToString(),
                latestSignal is null ? "-" : BuildRelativeTimeLabel(now, latestSignal.GeneratedAtUtc),
                latestVeto is null ? null : latestVeto.ReasonCode.ToString(),
                BuildStrategyLifecycleNote(strategy, runtimeVersion, latestVersion),
                definitionSummary.TemplateKey,
                definitionSummary.TemplateName,
                definitionSummary.ValidationStatusCode,
                definitionSummary.ValidationSummary,
                explainabilitySummary.ScoreLabel,
                explainabilitySummary.Summary,
                explainabilitySummary.RuleSummary,
                FormatVersionLabel(runtimeVersion),
                FormatVersionLabel(latestVersion),
                definitionSummary.TemplateRevisionLabel,
                latestVersion is null ? definitionSummary.TemplateRevisionLabel : BuildStrategyDefinitionSummary(latestVersion).TemplateRevisionLabel,
                BuildLifecycleTokenLabel(strategy));
        }).ToArray();

        var rows = allUsageRows
            .Where(row => string.IsNullOrWhiteSpace(normalizedQuery) ||
                          row.StrategyKey.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase) ||
                          row.DisplayName.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase) ||
                          row.LatestSignalType.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var totalSignals = signals.Count;
        var totalVetoes = vetoes.Count;
        var publishedStrategies = strategies.Count(strategy => strategy.PublishedMode.HasValue);
        var lowConfidenceCount = rows.Count(row => row.VetoRate >= 0.20m);
        var recentShadowCount = recentShadowDecisions.Count;
        var shadowFallbackCount = recentShadowDecisions.Count(entity => entity.AiIsFallback);
        var shadowAgreementCount = recentShadowDecisions.Count(entity => string.Equals(entity.AgreementState, "Agreement", StringComparison.Ordinal));
        var averageShadowAdvisoryScore = recentShadowCount == 0
            ? 0m
            : decimal.Round(recentShadowDecisions.Average(entity => entity.AiAdvisoryScore), 3, MidpointRounding.AwayFromZero);
        var shadowFallbackRate = recentShadowCount == 0
            ? 0m
            : decimal.Round((decimal)shadowFallbackCount / recentShadowCount * 100m, 1, MidpointRounding.AwayFromZero);
        var shadowAgreementRate = recentShadowCount == 0
            ? 0m
            : decimal.Round((decimal)shadowAgreementCount / recentShadowCount * 100m, 1, MidpointRounding.AwayFromZero);
        var averageShadowOutcomeScore = recentShadowOutcomes.Count == 0
            ? (decimal?)null
            : decimal.Round(recentShadowOutcomes.Average(entity => entity.OutcomeScore ?? 0m), 3, MidpointRounding.AwayFromZero);
        var latestShadowDecision = recentShadowDecisions.FirstOrDefault();
        var latestShadowProviderLabel = latestShadowDecision is null
            ? "No shadow feed"
            : string.IsNullOrWhiteSpace(latestShadowDecision.AiProviderModel)
                ? latestShadowDecision.AiProviderName
                : $"{latestShadowDecision.AiProviderName} / {latestShadowDecision.AiProviderModel}";
        var shadowHealthLabel = recentShadowCount == 0
            ? "NoShadowFeed"
            : shadowFallbackRate >= 25m
                ? "Degraded"
                : "Healthy";
        var shadowHealthTone = recentShadowCount == 0
            ? "neutral"
            : shadowFallbackRate >= 25m
                ? "warning"
                : "healthy";

        var summaryTiles = new[]
        {
            new AdminStatTileSnapshot("Strategy", strategies.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), "Tracked strategies", "info"),
            new AdminStatTileSnapshot("Signal", totalSignals.ToString(System.Globalization.CultureInfo.InvariantCulture), "Recent signal volume", "healthy"),
            new AdminStatTileSnapshot("Veto", totalVetoes.ToString(System.Globalization.CultureInfo.InvariantCulture), "Risk guard rejects", totalVetoes > 0 ? "warning" : "neutral"),
            new AdminStatTileSnapshot("Published", publishedStrategies.ToString(System.Globalization.CultureInfo.InvariantCulture), "Live / demo rollout", "info"),
            new AdminStatTileSnapshot("Low confidence", lowConfidenceCount.ToString(System.Globalization.CultureInfo.InvariantCulture), "Veto rate >= 20%", lowConfidenceCount > 0 ? "critical" : "healthy"),
            new AdminStatTileSnapshot("AI shadow", recentShadowCount.ToString(System.Globalization.CultureInfo.InvariantCulture), "Recent advisory decisions", recentShadowCount > 0 ? "info" : "neutral"),
            new AdminStatTileSnapshot("Avg advisory", recentShadowCount == 0 ? "n/a" : FormatSignedDecimal(averageShadowAdvisoryScore), "Recent shadow model score", recentShadowCount == 0 ? "neutral" : averageShadowAdvisoryScore >= 0m ? "healthy" : "warning")
        };

        var healthTiles = new[]
        {
            new AdminStatTileSnapshot("Model sağlığı", publishedStrategies > 0 ? "Guarded" : "Draft", publishedStrategies > 0 ? "Live / shadow mix" : "No published template", publishedStrategies > 0 ? "warning" : "neutral"),
            new AdminStatTileSnapshot("Veto yoğunluğu", totalSignals == 0 ? "0%" : $"{Math.Round((decimal)totalVetoes / Math.Max(totalSignals, 1) * 100, 1, MidpointRounding.AwayFromZero)}%", "Veto / signal ratio", totalVetoes > 0 ? "critical" : "healthy"),
            new AdminStatTileSnapshot("Explainability", rows.Any(row => row.LatestSignalType != "-") ? "Available" : "No signal", "Recent signal feed", rows.Any(row => row.LatestSignalType != "-") ? "info" : "neutral"),
            new AdminStatTileSnapshot("Freshness", rows.Any() ? BuildRelativeTimeLabel(now, signals.OrderByDescending(signal => signal.GeneratedAtUtc).FirstOrDefault()?.GeneratedAtUtc) : "n/a", "Latest generated signal", rows.Any() ? "healthy" : "neutral"),
            new AdminStatTileSnapshot("Shadow model", shadowHealthLabel, latestShadowProviderLabel, shadowHealthTone),
            new AdminStatTileSnapshot("Fallback rate", recentShadowCount == 0 ? "n/a" : $"{shadowFallbackRate:0.#}%", "Shadow provider fallback ratio", recentShadowCount == 0 ? "neutral" : shadowFallbackRate >= 25m ? "warning" : "healthy"),
            new AdminStatTileSnapshot("Agreement", recentShadowCount == 0 ? "n/a" : $"{shadowAgreementRate:0.#}%", "Strategy vs advisory alignment", recentShadowCount == 0 ? "neutral" : shadowAgreementRate >= 60m ? "healthy" : "warning"),
            new AdminStatTileSnapshot("Outcome avg", averageShadowOutcomeScore.HasValue ? FormatSignedDecimal(averageShadowOutcomeScore.Value) : "n/a", "Official +1 bar shadow outcome", averageShadowOutcomeScore is null ? "neutral" : averageShadowOutcomeScore >= 0m ? "healthy" : "warning")
        };

        var latestExplainability = BuildLatestStrategyExplainabilitySnapshot(strategies, versions, signals, vetoes);
        var templateCloneFacts = BuildTemplateCloneFacts(strategies, versions);
        var templateAdoptionSummary = BuildTemplateAdoptionSummary(now, templateSnapshots, templateCloneFacts, allUsageRows);
        var templateAdoptionRows = BuildTemplateAdoptionRows(now, templateSnapshots, templateCloneFacts, allUsageRows);
        var recentTemplateClones = BuildRecentTemplateClones(now, templateCloneFacts);

        return new AdminStrategyAiMonitoringPageSnapshot(
            normalizedQuery,
            summaryTiles,
            rows,
            healthTiles,
            templates,
            templateAdoptionSummary,
            templateAdoptionRows,
            recentTemplateClones,
            latestExplainability,
            now);
    }

    public async Task<AdminSupportLookupSnapshot> GetSupportLookupAsync(
        string? query = null,
        CancellationToken cancellationToken = default)
    {
        var now = UtcNow;
        var normalizedQuery = NormalizeOptional(query);

        if (string.IsNullOrWhiteSpace(normalizedQuery))
        {
            return AdminSupportLookupSnapshot.Empty(now);
        }

        var users = await dbContext.Users
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        var matchedUserEntities = users
            .Where(user => MatchesSupportQuery(user, normalizedQuery))
            .Take(6)
            .ToArray();
        var matchedUserIds = matchedUserEntities.Select(item => item.Id).ToArray();
        var matchedBots = await dbContext.TradingBots
            .AsNoTracking()
            .Where(bot => !bot.IsDeleted && matchedUserIds.Contains(bot.OwnerUserId))
            .OrderByDescending(bot => bot.UpdatedDate)
            .Take(6)
            .ToListAsync(cancellationToken);
        var matchedExchanges = await dbContext.ExchangeAccounts
            .AsNoTracking()
            .Where(account => !account.IsDeleted && matchedUserIds.Contains(account.OwnerUserId))
            .OrderByDescending(account => account.UpdatedDate)
            .Take(6)
            .ToListAsync(cancellationToken);
        var botCountLookup = matchedBots
            .GroupBy(bot => bot.OwnerUserId)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var exchangeCountLookup = matchedExchanges
            .GroupBy(account => account.OwnerUserId)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var matchedUsers = matchedUserEntities
            .Select(user =>
            {
                var activityAtUtc = user.MfaUpdatedAtUtc ?? user.TradingModeApprovedAtUtc;
                return new AdminUserListItemSnapshot(
                    user.Id,
                    string.IsNullOrWhiteSpace(user.FullName) ? user.UserName ?? user.Email ?? user.Id : user.FullName,
                    user.UserName ?? user.Id,
                    user.Email,
                    BuildUserStatusLabel(user),
                    BuildUserStatusTone(user),
                    BuildMfaLabel(user),
                    BuildMfaTone(user),
                    string.Empty,
                    BuildTradingModeLabel(user),
                    BuildTradingModeTone(user),
                    botCountLookup.TryGetValue(user.Id, out var botCount) ? botCount : 0,
                    exchangeCountLookup.TryGetValue(user.Id, out var exchangeCount) ? exchangeCount : 0,
                    activityAtUtc,
                    BuildRelativeTimeLabel(now, activityAtUtc),
                    BuildFreshnessTone(now, activityAtUtc));
            })
            .ToArray();

        var selectedUserId = matchedUserIds.FirstOrDefault();
        var diagnostics = string.IsNullOrWhiteSpace(selectedUserId)
            ? Array.Empty<AdminUserActivitySnapshot>()
            : (await LoadUserActivityFeedAsync(selectedUserId, cancellationToken)).Take(4).ToArray();
        var criticalEvents = (await LoadSecurityEventsAsync(normalizedQuery, "critical", null, cancellationToken))
            .Take(5)
            .Select(item => new AdminUserLogSnapshot(
                item.TimestampUtc,
                item.Summary,
                item.Module,
                item.TimeLabel,
                item.Severity,
                item.SeverityTone))
            .ToArray();
        var latestOrderFailures = await LoadLatestOrderFailuresAsync(cancellationToken);
        var botErrors = matchedBots.Select(bot =>
            latestOrderFailures.TryGetValue(bot.Id, out var failure)
                ? new AdminBotOperationSnapshot(
                    bot.Id.ToString(),
                    bot.Name,
                    bot.OwnerUserId,
                    matchedUsers.FirstOrDefault(user => user.UserId == bot.OwnerUserId)?.DisplayName ?? bot.OwnerUserId,
                    bot.IsEnabled ? "Aktif" : "Durduruldu",
                    bot.IsEnabled ? "healthy" : "neutral",
                    BuildTradingModeLabel(bot.TradingModeOverride ?? ExecutionEnvironment.Demo),
                    BuildTradingModeTone(bot.TradingModeOverride ?? ExecutionEnvironment.Demo),
                    bot.StrategyKey,
                    bot.OpenPositionCount > 0 ? "Exposure var" : "Exposure yok",
                    bot.OpenPositionCount > 0 ? "warning" : "neutral",
                    bot.IsEnabled ? "Çalışıyor" : "Durduruldu",
                    failure,
                    bot.OpenOrderCount,
                    bot.OpenPositionCount)
                : null)
            .Where(bot => bot is not null)
            .Select(bot => bot!)
            .Take(4)
            .ToArray();

        var summaryTiles = new[]
        {
            new AdminStatTileSnapshot("Kullanıcı", matchedUsers.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), "Exact / partial match", "info"),
            new AdminStatTileSnapshot("Bot", matchedBots.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), "Owned bots", matchedBots.Count > 0 ? "healthy" : "neutral"),
            new AdminStatTileSnapshot("Exchange", matchedExchanges.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), "Owned exchange accounts", matchedExchanges.Count > 0 ? "warning" : "neutral"),
            new AdminStatTileSnapshot("Kritik olay", criticalEvents.Count().ToString(System.Globalization.CultureInfo.InvariantCulture), "Recent related audit events", criticalEvents.Any() ? "critical" : "healthy")
        };

        return new AdminSupportLookupSnapshot(
            normalizedQuery,
            matchedUsers.Length == 0 ? "Eşleşme bulunamadı" : $"Eşleşen kullanıcı: {matchedUsers[0].DisplayName}",
            matchedUsers.Length == 0
                ? "Arama sonuçları yok. Kullanıcı, bot, exchange veya güvenlik kaydı ile tekrar deneyin."
                : "Lookup sonucu gerçek kullanıcı ve bağlı kaynaklardan okunur; secret/PII maskeli kalır.",
            summaryTiles,
            matchedUsers,
            diagnostics,
            criticalEvents,
            botErrors,
            now);
    }

    public async Task<AdminSecurityEventsPageSnapshot> GetSecurityEventsAsync(
        string? query = null,
        string? severity = null,
        string? module = null,
        CancellationToken cancellationToken = default)
    {
        var now = UtcNow;
        var normalizedQuery = NormalizeOptional(query);
        var normalizedSeverity = NormalizeOptional(severity);
        var normalizedModule = NormalizeOptional(module);

        var events = await LoadSecurityEventsAsync(normalizedQuery, normalizedSeverity, normalizedModule, cancellationToken);

        var summaryTiles = new[]
        {
            new AdminStatTileSnapshot("Toplam event", events.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), "Security / audit events", "info"),
            new AdminStatTileSnapshot("Critical", events.Count(item => item.SeverityTone == "critical").ToString(System.Globalization.CultureInfo.InvariantCulture), "High severity alerts", "critical"),
            new AdminStatTileSnapshot("Warning", events.Count(item => item.SeverityTone == "warning").ToString(System.Globalization.CultureInfo.InvariantCulture), "Watch list", "warning"),
            new AdminStatTileSnapshot("Masked actor", events.Count(item => item.Actor.Contains('*', StringComparison.Ordinal)).ToString(System.Globalization.CultureInfo.InvariantCulture), "PII safe actors", "healthy"),
            new AdminStatTileSnapshot("Module", events.Select(item => item.Module).Distinct(StringComparer.OrdinalIgnoreCase).Count().ToString(System.Globalization.CultureInfo.InvariantCulture), "Observed modules", "info")
        };

        return new AdminSecurityEventsPageSnapshot(normalizedQuery, normalizedSeverity, normalizedModule, summaryTiles, events, now);
    }

    public async Task<AdminNotificationsPageSnapshot> GetNotificationsAsync(
        string? severity = null,
        string? category = null,
        CancellationToken cancellationToken = default)
    {
        var now = UtcNow;
        var normalizedSeverity = NormalizeOptional(severity);
        var normalizedCategory = NormalizeOptional(category);

        var monitoringSnapshot = await monitoringReadModelService.GetSnapshotAsync(cancellationToken);
        var incidents = await dbContext.Incidents
            .AsNoTracking()
            .Where(incident => !incident.IsDeleted && incident.Status != IncidentStatus.Resolved)
            .OrderByDescending(incident => incident.CreatedDate)
            .Take(20)
            .ToListAsync(cancellationToken);

        var alerts = new List<AdminNotificationSnapshot>();

        foreach (var health in monitoringSnapshot.HealthSnapshots)
        {
            var state = GetNotificationState(health.HealthState, health.FreshnessTier, health.CircuitBreakerState);

            if (state.Tone == "healthy")
            {
                continue;
            }

            alerts.Add(new AdminNotificationSnapshot(
                health.LastUpdatedAtUtc,
                BuildRelativeTimeLabel(now, health.LastUpdatedAtUtc),
                health.DisplayName,
                "Health",
                state.CategoryTone,
                state.Label,
                state.Tone,
                health.SentinelName,
                state.Label,
                state.Tone,
                health.Detail ?? state.Detail,
                health.CircuitBreakerState.ToString()));
        }

        foreach (var incident in incidents)
        {
            alerts.Add(new AdminNotificationSnapshot(
                incident.CreatedDate,
                BuildRelativeTimeLabel(now, incident.CreatedDate),
                incident.Title,
                "Incident",
                incident.Severity switch
                {
                    IncidentSeverity.Critical => "critical",
                    IncidentSeverity.Warning => "warning",
                    _ => "info"
                },
                incident.Severity.ToString(),
                incident.Severity switch
                {
                    IncidentSeverity.Critical => "critical",
                    IncidentSeverity.Warning => "warning",
                    _ => "info"
                },
                incident.OperationType?.ToString() ?? "Incident",
                incident.Status.ToString(),
                incident.Status switch
                {
                    IncidentStatus.Open => "warning",
                    IncidentStatus.Monitoring => "info",
                    _ => "healthy"
                },
                incident.Summary,
                incident.TargetType ?? "Global"));
        }

        var filteredAlerts = alerts
            .Where(alert => string.IsNullOrWhiteSpace(normalizedSeverity) || alert.Severity.Contains(normalizedSeverity, StringComparison.OrdinalIgnoreCase))
            .Where(alert => string.IsNullOrWhiteSpace(normalizedCategory) || alert.Category.Contains(normalizedCategory, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(alert => alert.TimestampUtc)
            .Take(50)
            .ToArray();

        var summaryTiles = new[]
        {
            new AdminStatTileSnapshot("Alarm", filteredAlerts.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), "Current notifications", "warning"),
            new AdminStatTileSnapshot("Critical", filteredAlerts.Count(alert => alert.SeverityTone == "critical").ToString(System.Globalization.CultureInfo.InvariantCulture), "Needs attention", "critical"),
            new AdminStatTileSnapshot("Warning", filteredAlerts.Count(alert => alert.SeverityTone == "warning").ToString(System.Globalization.CultureInfo.InvariantCulture), "Watch list", "warning"),
            new AdminStatTileSnapshot("Health state", monitoringSnapshot.HealthSnapshots.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), "Monitored sentinels", "info")
        };

        return new AdminNotificationsPageSnapshot(normalizedSeverity, normalizedCategory, summaryTiles, filteredAlerts, now);
    }

    private AdminStrategyExplainabilitySnapshot BuildLatestStrategyExplainabilitySnapshot(
        IReadOnlyCollection<TradingStrategy> strategies,
        IReadOnlyCollection<TradingStrategyVersion> versions,
        IReadOnlyCollection<TradingStrategySignal> signals,
        IReadOnlyCollection<TradingStrategySignalVeto> vetoes)
    {
        var latestSignal = signals.OrderByDescending(signal => signal.GeneratedAtUtc).FirstOrDefault();
        var latestVeto = vetoes.OrderByDescending(veto => veto.EvaluatedAtUtc).FirstOrDefault();

        if (latestSignal is null && latestVeto is null)
        {
            return new AdminStrategyExplainabilitySnapshot(
                "n/a",
                "n/a",
                "n/a",
                "NotEvaluated",
                "n/a",
                "Henüz strategy explainability snapshot'u oluşmadı.",
                "n/a",
                "custom",
                null);
        }

        if (latestVeto is null || (latestSignal is not null && latestSignal.GeneratedAtUtc >= latestVeto.EvaluatedAtUtc))
        {
            var strategy = strategies.FirstOrDefault(item => item.Id == latestSignal!.TradingStrategyId);
            var version = versions.FirstOrDefault(item => item.Id == latestSignal!.TradingStrategyVersionId);
            var definitionSummary = BuildStrategyDefinitionSummary(version);
            var confidence = TryDeserialize<StrategySignalConfidenceSnapshot>(latestSignal!.RiskEvaluationJson)
                ?? new StrategySignalConfidenceSnapshot(0, StrategySignalConfidenceBand.Low, 0, 0, true, false, false, RiskVetoReasonCode.None, false, "Signal confidence unavailable.");
            var evaluationResult = TryDeserialize<StrategyEvaluationResult>(latestSignal.RuleResultSnapshotJson);

            return new AdminStrategyExplainabilitySnapshot(
                strategy?.StrategyKey ?? "n/a",
                latestSignal.Symbol,
                latestSignal.Timeframe,
                latestSignal.SignalType.ToString(),
                FormattableString.Invariant($"{confidence.ScorePercentage}/100"),
                confidence.Summary,
                BuildRuleSummary(evaluationResult),
                definitionSummary.TemplateName,
                NormalizeUtc(latestSignal.GeneratedAtUtc),
                definitionSummary.TemplateRevisionLabel,
                FormatVersionLabel(version));
        }

        var vetoStrategy = strategies.FirstOrDefault(item => item.Id == latestVeto.TradingStrategyId);
        var vetoVersion = versions.FirstOrDefault(item => item.Id == latestVeto.TradingStrategyVersionId);
        var vetoDefinitionSummary = BuildStrategyDefinitionSummary(vetoVersion);
        var vetoConfidence = TryDeserialize<StrategySignalConfidenceSnapshot>(latestVeto.RiskEvaluationJson)
            ?? new StrategySignalConfidenceSnapshot(0, StrategySignalConfidenceBand.Low, 0, 0, true, false, true, latestVeto.ReasonCode, false, latestVeto.ReasonCode.ToString());

        return new AdminStrategyExplainabilitySnapshot(
            vetoStrategy?.StrategyKey ?? "n/a",
            latestVeto.Symbol,
            latestVeto.Timeframe,
            $"Vetoed:{latestVeto.ReasonCode}",
            FormattableString.Invariant($"{vetoConfidence.ScorePercentage}/100"),
            vetoConfidence.Summary,
            $"RiskVeto={latestVeto.ReasonCode}; {vetoConfidence.Summary}",
            vetoDefinitionSummary.TemplateName,
            NormalizeUtc(latestVeto.EvaluatedAtUtc),
            vetoDefinitionSummary.TemplateRevisionLabel,
            FormatVersionLabel(vetoVersion));
    }

    private StrategyDefinitionSummary BuildStrategyDefinitionSummary(TradingStrategyVersion? version)
    {
        if (version is null)
        {
            return new StrategyDefinitionSummary("custom", "Custom strategy", "MissingVersion", "Published strategy version was not found.", "n/a", null);
        }

        try
        {
            var document = strategyRuleParser.Parse(version.DefinitionJson);
            var validation = strategyDefinitionValidator.Validate(document);
            var templateKey = string.IsNullOrWhiteSpace(document.Metadata?.TemplateKey)
                ? "custom"
                : document.Metadata!.TemplateKey!.Trim();
            var templateName = string.IsNullOrWhiteSpace(document.Metadata?.TemplateName)
                ? "Custom strategy"
                : document.Metadata!.TemplateName!.Trim();

            return new StrategyDefinitionSummary(
                templateKey,
                templateName,
                validation.StatusCode,
                validation.Summary,
                BuildTemplateRevisionLabel(document.Metadata?.TemplateRevisionNumber),
                document.Metadata?.TemplateSource);
        }
        catch (StrategyDefinitionValidationException exception)
        {
            return new StrategyDefinitionSummary("custom", "Custom strategy", exception.StatusCode, exception.Message, "n/a", null);
        }
        catch (StrategyRuleParseException exception)
        {
            return new StrategyDefinitionSummary("custom", "Custom strategy", "ParseFailed", exception.Message, "n/a", null);
        }
    }

    private StrategyExplainabilitySummaryResult BuildStrategyExplainabilitySummary(
        TradingStrategySignal? latestSignal,
        TradingStrategySignalVeto? latestVeto)
    {
        if (latestSignal is null && latestVeto is null)
        {
            return new StrategyExplainabilitySummaryResult("n/a", "Henüz sinyal yok.", "n/a");
        }

        if (latestVeto is null || (latestSignal is not null && latestSignal.GeneratedAtUtc >= latestVeto.EvaluatedAtUtc))
        {
            var confidence = TryDeserialize<StrategySignalConfidenceSnapshot>(latestSignal!.RiskEvaluationJson)
                ?? new StrategySignalConfidenceSnapshot(0, StrategySignalConfidenceBand.Low, 0, 0, true, false, false, RiskVetoReasonCode.None, false, "Signal confidence unavailable.");
            var evaluationResult = TryDeserialize<StrategyEvaluationResult>(latestSignal.RuleResultSnapshotJson);

            return new StrategyExplainabilitySummaryResult(
                FormattableString.Invariant($"{confidence.ScorePercentage}/100"),
                confidence.Summary,
                BuildRuleSummary(evaluationResult));
        }

        var vetoConfidence = TryDeserialize<StrategySignalConfidenceSnapshot>(latestVeto.RiskEvaluationJson)
            ?? new StrategySignalConfidenceSnapshot(0, StrategySignalConfidenceBand.Low, 0, 0, true, false, true, latestVeto.ReasonCode, false, latestVeto.ReasonCode.ToString());

        return new StrategyExplainabilitySummaryResult(
            FormattableString.Invariant($"{vetoConfidence.ScorePercentage}/100"),
            vetoConfidence.Summary,
            $"RiskVeto={latestVeto.ReasonCode}; {vetoConfidence.Summary}");
    }

    private static string BuildRuleSummary(StrategyEvaluationResult? evaluationResult)
    {
        if (evaluationResult is null)
        {
            return "Rule snapshot unavailable.";
        }

        var passedRules = EnumerateLeafRules(evaluationResult.EntryRuleResult)
            .Concat(EnumerateLeafRules(evaluationResult.ExitRuleResult))
            .Concat(EnumerateLeafRules(evaluationResult.RiskRuleResult))
            .Where(rule => rule.Enabled && rule.Matched)
            .Select(DescribeRule)
            .Take(2)
            .ToArray();
        var failedRules = EnumerateLeafRules(evaluationResult.EntryRuleResult)
            .Concat(EnumerateLeafRules(evaluationResult.ExitRuleResult))
            .Concat(EnumerateLeafRules(evaluationResult.RiskRuleResult))
            .Where(rule => rule.Enabled && !rule.Matched)
            .Select(DescribeRule)
            .Take(2)
            .ToArray();

        return $"Passed={string.Join(" | ", passedRules.DefaultIfEmpty("none"))}; Failed={string.Join(" | ", failedRules.DefaultIfEmpty("none"))}";
    }

    private static IEnumerable<StrategyRuleResultSnapshot> EnumerateLeafRules(StrategyRuleResultSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            yield break;
        }

        if (snapshot.Children.Count == 0)
        {
            yield return snapshot;
            yield break;
        }

        foreach (var child in snapshot.Children.SelectMany(EnumerateLeafRules))
        {
            yield return child;
        }
    }

    private static string DescribeRule(StrategyRuleResultSnapshot snapshot)
    {
        var label = string.IsNullOrWhiteSpace(snapshot.RuleId)
            ? snapshot.Path ?? "rule"
            : snapshot.RuleId!.Trim();
        var reason = string.IsNullOrWhiteSpace(snapshot.Reason)
            ? snapshot.Matched ? "PASS" : "FAIL"
            : snapshot.Reason!.Trim();

        return $"{label}:{reason}";
    }

    private static TSnapshot? TryDeserialize<TSnapshot>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return default;
        }

        try
        {
            return JsonSerializer.Deserialize<TSnapshot>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static DateTime NormalizeUtc(DateTime value)
    {
        return value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
    }

    private static string BuildTradingModeLabel(ExecutionEnvironment mode) =>
        mode == ExecutionEnvironment.Live ? "Live" : "Demo";

    private static string BuildTradingModeTone(ExecutionEnvironment mode) =>
        mode == ExecutionEnvironment.Live ? "critical" : "neutral";

    private static string BuildTemplateRevisionLabel(int? revisionNumber)
    {
        return revisionNumber is > 0
            ? $"r{revisionNumber.Value}"
            : "n/a";
    }

    private static string FormatVersionLabel(TradingStrategyVersion? version)
    {
        return version is null
            ? "Inactive"
            : $"v{version.VersionNumber}";
    }

    private static string BuildLifecycleTokenLabel(TradingStrategy strategy)
    {
        if (strategy.ActivationConcurrencyToken.Length == 0)
        {
            return "n/a";
        }

        var encoded = Convert.ToBase64String(strategy.ActivationConcurrencyToken);
        return encoded.Length <= 12
            ? encoded
            : $"{encoded[..6]}...{encoded[^4..]}";
    }

    private static string BuildTemplateLineageLabel(StrategyTemplateSnapshot template)
    {
        var sourceLabel = string.IsNullOrWhiteSpace(template.SourceTemplateKey)
            ? template.TemplateSource
            : $"{template.SourceTemplateKey}/r{template.SourceRevisionNumber ?? 1}";

        var activeLabel = template.IsActive
            ? $"r{template.ActiveRevisionNumber}"
            : "inactive";

        return $"{sourceLabel}; Published=r{template.PublishedRevisionNumber}; Active={activeLabel}; Latest=r{template.LatestRevisionNumber}";
    }

    private IReadOnlyCollection<TemplateCloneFact> BuildTemplateCloneFacts(
        IReadOnlyCollection<TradingStrategy> strategies,
        IReadOnlyCollection<TradingStrategyVersion> versions)
    {
        var strategyLookup = strategies.ToDictionary(strategy => strategy.Id, strategy => strategy);

        return versions
            .Select(version =>
            {
                strategyLookup.TryGetValue(version.TradingStrategyId, out var strategy);
                var definitionSummary = BuildStrategyDefinitionSummary(version);
                if (string.IsNullOrWhiteSpace(definitionSummary.TemplateKey) ||
                    string.Equals(definitionSummary.TemplateKey, "custom", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                return new TemplateCloneFact(
                    definitionSummary.TemplateKey,
                    definitionSummary.TemplateName,
                    strategy?.StrategyKey ?? "n/a",
                    strategy?.DisplayName ?? (strategy?.StrategyKey ?? version.TradingStrategyId.ToString()),
                    FormatVersionLabel(version),
                    definitionSummary.TemplateRevisionLabel,
                    version.CreatedDate,
                    definitionSummary.ValidationStatusCode,
                    definitionSummary.ValidationSummary);
            })
            .Where(fact => fact is not null)
            .Select(fact => fact!)
            .ToArray();
    }

    private AdminTemplateAdoptionSummarySnapshot BuildTemplateAdoptionSummary(
        DateTime now,
        IReadOnlyCollection<StrategyTemplateSnapshot> templates,
        IReadOnlyCollection<TemplateCloneFact> cloneFacts,
        IReadOnlyCollection<AdminStrategyUsageSnapshot> usageRows)
    {
        DateTime? lastCloneAtUtc = cloneFacts.Count == 0
            ? null
            : cloneFacts.Max(fact => fact.CreatedAtUtc);
        var mostUsedTemplate = cloneFacts
            .GroupBy(fact => new { fact.TemplateKey, fact.TemplateName })
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key.TemplateName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        var latestValidationIssue = templates
            .Where(template => !string.Equals(template.Validation.StatusCode, "Valid", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(template => template.UpdatedAtUtc ?? template.CreatedAtUtc ?? DateTime.MinValue)
            .FirstOrDefault();
        var activeTemplateStrategyCount = usageRows
            .Where(row => !string.Equals(row.TemplateKey, "custom", StringComparison.OrdinalIgnoreCase) &&
                          !string.Equals(row.RuntimeVersionLabel, "Inactive", StringComparison.OrdinalIgnoreCase))
            .Select(row => row.StrategyKey)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        return new AdminTemplateAdoptionSummarySnapshot(
            templates.Count,
            templates.Count(template => template.PublishedRevisionNumber > 0),
            templates.Count(template => !template.IsActive),
            cloneFacts.Count,
            lastCloneAtUtc,
            BuildRelativeTimeLabel(now, lastCloneAtUtc),
            mostUsedTemplate is null
                ? "n/a"
                : $"{mostUsedTemplate.Key.TemplateName} ({mostUsedTemplate.Count().ToString(System.Globalization.CultureInfo.InvariantCulture)})",
            activeTemplateStrategyCount,
            latestValidationIssue is null
                ? "No validation issue"
                : $"{latestValidationIssue.TemplateName} · {latestValidationIssue.Validation.StatusCode} · {latestValidationIssue.Validation.Summary}");
    }

    private IReadOnlyCollection<AdminTemplateAdoptionRowSnapshot> BuildTemplateAdoptionRows(
        DateTime now,
        IReadOnlyCollection<StrategyTemplateSnapshot> templates,
        IReadOnlyCollection<TemplateCloneFact> cloneFacts,
        IReadOnlyCollection<AdminStrategyUsageSnapshot> usageRows)
    {
        var templateLookup = templates.ToDictionary(template => template.TemplateKey, template => template, StringComparer.OrdinalIgnoreCase);
        var activeUsageLookup = usageRows
            .Where(row => !string.Equals(row.TemplateKey, "custom", StringComparison.OrdinalIgnoreCase) &&
                          !string.Equals(row.RuntimeVersionLabel, "Inactive", StringComparison.OrdinalIgnoreCase))
            .GroupBy(row => row.TemplateKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Select(row => row.StrategyKey).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                StringComparer.OrdinalIgnoreCase);

        return cloneFacts
            .GroupBy(fact => fact.TemplateKey, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                templateLookup.TryGetValue(group.Key, out var template);
                activeUsageLookup.TryGetValue(group.Key, out var activeStrategyCount);
                var lastCloneAtUtc = group.Max(item => item.CreatedAtUtc);
                var latestClone = group.OrderByDescending(item => item.CreatedAtUtc).First();

                return new AdminTemplateAdoptionRowSnapshot(
                    group.Key,
                    template?.TemplateName ?? latestClone.TemplateName,
                    group.Count(),
                    activeStrategyCount,
                    BuildRelativeTimeLabel(now, lastCloneAtUtc),
                    template is null ? "Unknown" : template.IsActive ? "Active" : "Archived",
                    template?.Validation.StatusCode ?? latestClone.ValidationStatusCode);
            })
            .OrderByDescending(row => row.CloneCount)
            .ThenBy(row => row.TemplateName, StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToArray();
    }

    private IReadOnlyCollection<AdminRecentTemplateCloneSnapshot> BuildRecentTemplateClones(
        DateTime now,
        IReadOnlyCollection<TemplateCloneFact> cloneFacts)
    {
        return cloneFacts
            .OrderByDescending(fact => fact.CreatedAtUtc)
            .ThenBy(fact => fact.StrategyDisplayName, StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .Select(fact => new AdminRecentTemplateCloneSnapshot(
                fact.StrategyKey,
                fact.StrategyDisplayName,
                fact.TemplateKey,
                fact.TemplateName,
                fact.VersionLabel,
                fact.TemplateRevisionLabel,
                BuildRelativeTimeLabel(now, fact.CreatedAtUtc)))
            .ToArray();
    }

    private sealed record TemplateCloneFact(
        string TemplateKey,
        string TemplateName,
        string StrategyKey,
        string StrategyDisplayName,
        string VersionLabel,
        string TemplateRevisionLabel,
        DateTime CreatedAtUtc,
        string ValidationStatusCode,
        string ValidationSummary);

    private sealed record StrategyDefinitionSummary(
        string TemplateKey,
        string TemplateName,
        string ValidationStatusCode,
        string ValidationSummary,
        string TemplateRevisionLabel,
        string? TemplateSource);

    private sealed record StrategyExplainabilitySummaryResult(
        string ScoreLabel,
        string Summary,
        string RuleSummary);

    private static string BuildStrategyHealthLabel(TradingStrategy strategy, int signalCount, int vetoCount)
    {
        if (strategy.UsesExplicitVersionLifecycle && !strategy.ActiveTradingStrategyVersionId.HasValue)
        {
            return "Inactive";
        }

        if (strategy.PublishedMode.HasValue)
        {
            return vetoCount > signalCount / 3 ? "Guarded" : "Live";
        }

        return signalCount == 0 ? "Draft" : "Monitoring";
    }

    private static string FormatSignedDecimal(decimal value)
    {
        return value > 0m
            ? $"+{value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}"
            : value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string BuildStrategyHealthTone(TradingStrategy strategy, int signalCount, int vetoCount)
    {
        if (strategy.UsesExplicitVersionLifecycle && !strategy.ActiveTradingStrategyVersionId.HasValue)
        {
            return "warning";
        }

        if (strategy.PublishedMode.HasValue)
        {
            return vetoCount > signalCount / 3 ? "warning" : "healthy";
        }

        return signalCount == 0 ? "neutral" : "info";
    }

    private static TradingStrategyVersion? ResolveRuntimeStrategyVersion(
        TradingStrategy strategy,
        IReadOnlyCollection<TradingStrategyVersion> versions)
    {
        var publishedVersions = versions
            .Where(version => version.Status == StrategyVersionStatus.Published)
            .OrderByDescending(version => version.VersionNumber)
            .ToArray();

        if (strategy.UsesExplicitVersionLifecycle)
        {
            return strategy.ActiveTradingStrategyVersionId.HasValue
                ? publishedVersions.FirstOrDefault(version => version.Id == strategy.ActiveTradingStrategyVersionId.Value)
                : null;
        }

        return publishedVersions.FirstOrDefault();
    }

    private static string BuildStrategyLifecycleNote(
        TradingStrategy strategy,
        TradingStrategyVersion? runtimeVersion,
        TradingStrategyVersion? latestVersion)
    {
        var promotionLabel = strategy.PublishedMode is null ? "Draft / shadow" : $"Published {strategy.PublishedMode}";

        if (runtimeVersion is null)
        {
            return strategy.UsesExplicitVersionLifecycle
                ? $"{promotionLabel}; Runtime=Inactive"
                : promotionLabel;
        }

        var latestVersionLabel = latestVersion is null ? "n/a" : $"v{latestVersion.VersionNumber}";
        return $"{promotionLabel}; Runtime=v{runtimeVersion.VersionNumber}; Latest={latestVersionLabel}";
    }

    private static string BuildEventTone(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "neutral";
        }

        return value.Contains("Failed", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("Blocked", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("Rejected", StringComparison.OrdinalIgnoreCase)
            ? "critical"
            : "info";
    }

    private static string BuildAuditSummary(string action, string target, string? context)
    {
        var summary = $"{action} · {target}";
        return string.IsNullOrWhiteSpace(context) ? summary : $"{summary} · {context}";
    }

    private static string BuildCredentialTone(ExchangeCredentialStatus status)
    {
        return status switch
        {
            ExchangeCredentialStatus.Active => "healthy",
            ExchangeCredentialStatus.PendingValidation => "warning",
            ExchangeCredentialStatus.RevalidationRequired => "warning",
            ExchangeCredentialStatus.RotationRequired => "critical",
            ExchangeCredentialStatus.Invalid => "critical",
            ExchangeCredentialStatus.Missing => "warning",
            _ => "neutral"
        };
    }

    private static string MaskFingerprint(string? fingerprint)
    {
        if (string.IsNullOrWhiteSpace(fingerprint))
        {
            return "missing";
        }

        var normalized = fingerprint.Trim();
        if (normalized.Length <= 8)
        {
            return normalized;
        }

        return $"{normalized[..4]}...{normalized[^4..]}";
    }

    private static string MaskActor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "masked";
        }

        var normalized = value.Trim();
        if (normalized.Length <= 6)
        {
            return normalized;
        }

        return $"{normalized[..3]}***{normalized[^2..]}";
    }

    private static bool MatchesSupportQuery(ApplicationUser user, string query)
    {
        return user.Id.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               (user.UserName?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
               (user.Email?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
               (user.FullName?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
               (user.PhoneNumber?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private static string[] BuildSecurityTags(string action, string target)
    {
        var tags = new List<string>();

        if (action.Contains("MFA", StringComparison.OrdinalIgnoreCase))
        {
            tags.Add("mfa");
        }

        if (action.Contains("Login", StringComparison.OrdinalIgnoreCase))
        {
            tags.Add("login");
        }

        if (action.Contains("Trade", StringComparison.OrdinalIgnoreCase) || target.Contains("Trade", StringComparison.OrdinalIgnoreCase))
        {
            tags.Add("trading");
        }

        if (action.Contains("Admin", StringComparison.OrdinalIgnoreCase))
        {
            tags.Add("admin");
        }

        if (tags.Count == 0)
        {
            tags.Add("audit");
        }

        return tags.ToArray();
    }

    private static string ClassifySecurityCategory(string value)
    {
        if (value.Contains("Login", StringComparison.OrdinalIgnoreCase) || value.Contains("MFA", StringComparison.OrdinalIgnoreCase))
        {
            return "Security";
        }

        if (value.Contains("Trade", StringComparison.OrdinalIgnoreCase))
        {
            return "Trading";
        }

        if (value.Contains("Admin", StringComparison.OrdinalIgnoreCase))
        {
            return "Admin";
        }

        return "Audit";
    }

    private static string ClassifySecurityCategoryTone(string value)
    {
        if (value.Contains("Login", StringComparison.OrdinalIgnoreCase) || value.Contains("MFA", StringComparison.OrdinalIgnoreCase))
        {
            return "critical";
        }

        if (value.Contains("Trade", StringComparison.OrdinalIgnoreCase))
        {
            return "warning";
        }

        if (value.Contains("Admin", StringComparison.OrdinalIgnoreCase))
        {
            return "info";
        }

        return "neutral";
    }

    private static string ClassifySecuritySeverity(string action, string outcome)
    {
        if (action.Contains("Failed", StringComparison.OrdinalIgnoreCase) ||
            action.Contains("Blocked", StringComparison.OrdinalIgnoreCase) ||
            action.Contains("Rejected", StringComparison.OrdinalIgnoreCase) ||
            outcome.Contains("Failed", StringComparison.OrdinalIgnoreCase))
        {
            return "Critical";
        }

        if (action.Contains("Warn", StringComparison.OrdinalIgnoreCase) ||
            action.Contains("Risk", StringComparison.OrdinalIgnoreCase))
        {
            return "Warning";
        }

        return "Info";
    }

    private static string ClassifySecuritySeverityTone(string action, string outcome)
    {
        return ClassifySecuritySeverity(action, outcome) switch
        {
            "Critical" => "critical",
            "Warning" => "warning",
            _ => "info"
        };
    }

    private static string ClassifySecurityModule(string? target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return "Global";
        }

        if (target.Contains("MFA", StringComparison.OrdinalIgnoreCase) || target.Contains("Login", StringComparison.OrdinalIgnoreCase))
        {
            return "Auth";
        }

        if (target.Contains("Trade", StringComparison.OrdinalIgnoreCase))
        {
            return "Trading";
        }

        if (target.Contains("Policy", StringComparison.OrdinalIgnoreCase))
        {
            return "Policy";
        }

        if (target.Contains("Admin", StringComparison.OrdinalIgnoreCase))
        {
            return "Admin";
        }

        return target;
    }

    private static NotificationState GetNotificationState(
        MonitoringHealthState healthState,
        MonitoringFreshnessTier freshnessTier,
        CircuitBreakerStateCode breakerState)
    {
        if (healthState == MonitoringHealthState.Critical || freshnessTier == MonitoringFreshnessTier.Stale)
        {
            return new NotificationState("Critical", "critical", "Stale data / breaker mismatch", "critical");
        }

        if (healthState == MonitoringHealthState.Degraded || breakerState != CircuitBreakerStateCode.Closed)
        {
            return new NotificationState("Warning", "warning", "Degraded or cooling down", "warning");
        }

        return new NotificationState("Healthy", "healthy", "Stable", "info");
    }

    private sealed record NotificationState(string Label, string Tone, string Detail, string CategoryTone);

    private async Task<IReadOnlyCollection<AdminSecurityEventSnapshot>> LoadSecurityEventsAsync(
        string? query,
        string? severityFilter,
        string? moduleFilter,
        CancellationToken cancellationToken)
    {
        var normalizedQuery = NormalizeOptional(query);
        var normalizedSeverity = NormalizeOptional(severityFilter);
        var normalizedModule = NormalizeOptional(moduleFilter);

        var auditLogs = await dbContext.AuditLogs
            .AsNoTracking()
            .Where(log => !log.IsDeleted)
            .OrderByDescending(log => log.CreatedDate)
            .Take(100)
            .ToListAsync(cancellationToken);
        var adminLogs = await dbContext.AdminAuditLogs
            .AsNoTracking()
            .OrderByDescending(log => log.CreatedAtUtc)
            .Take(100)
            .ToListAsync(cancellationToken);

        return auditLogs.Select(log => new AdminSecurityEventSnapshot(
                log.CreatedDate,
                BuildRelativeTimeLabel(UtcNow, log.CreatedDate),
                ClassifySecurityCategory(log.Action),
                ClassifySecurityCategoryTone(log.Action),
                ClassifySecuritySeverity(log.Action, log.Outcome),
                ClassifySecuritySeverityTone(log.Action, log.Outcome),
                MaskActor(log.Actor),
                ClassifySecurityModule(log.Target),
                log.Action,
                log.Context ?? log.Outcome,
                BuildSecurityTags(log.Action, log.Target)))
            .Concat(adminLogs.Select(log => new AdminSecurityEventSnapshot(
                log.CreatedAtUtc,
                BuildRelativeTimeLabel(UtcNow, log.CreatedAtUtc),
                ClassifySecurityCategory(log.ActionType),
                ClassifySecurityCategoryTone(log.ActionType),
                ClassifySecuritySeverity(log.ActionType, log.Reason),
                ClassifySecuritySeverityTone(log.ActionType, log.Reason),
                MaskActor(log.ActorUserId),
                ClassifySecurityModule(log.TargetType),
                log.ActionType,
                BuildAuditSummary(log.ActionType, log.TargetType, log.Reason),
                BuildSecurityTags(log.ActionType, log.TargetType))))
            .Where(item => string.IsNullOrWhiteSpace(normalizedQuery) ||
                           item.Category.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase) ||
                           item.Severity.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase) ||
                           item.Actor.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase) ||
                           item.Module.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase) ||
                           item.Summary.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase))
            .Where(item => string.IsNullOrWhiteSpace(normalizedSeverity) ||
                           item.Severity.Contains(normalizedSeverity, StringComparison.OrdinalIgnoreCase))
            .Where(item => string.IsNullOrWhiteSpace(normalizedModule) ||
                           item.Module.Contains(normalizedModule, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.TimestampUtc)
            .Take(50)
            .ToArray();
    }

    private async Task<Dictionary<Guid, string>> LoadLatestOrderFailuresAsync(CancellationToken cancellationToken)
    {
        var latestFailures = await dbContext.ExecutionOrders
            .AsNoTracking()
            .Where(order => !order.IsDeleted && order.FailureCode != null)
            .OrderByDescending(order => order.UpdatedDate)
            .Select(order => new { order.BotId, order.FailureCode, order.FailureDetail })
            .ToListAsync(cancellationToken);

        return latestFailures
            .Where(item => item.BotId.HasValue)
            .GroupBy(item => item.BotId!.Value)
            .ToDictionary(
                group => group.Key,
                group =>
                {
                    var latest = group.First();
                    return string.IsNullOrWhiteSpace(latest.FailureDetail)
                        ? latest.FailureCode ?? "No failure"
                        : $"{latest.FailureCode}: {latest.FailureDetail}";
                });
    }

    private static string BuildSecuritySummary(AdminSecurityEventSnapshot snapshot)
    {
        var tags = snapshot.Tags.Count == 0 ? string.Empty : $" · {string.Join(", ", snapshot.Tags)}";
        return $"{snapshot.Summary}{tags}";
    }
}










