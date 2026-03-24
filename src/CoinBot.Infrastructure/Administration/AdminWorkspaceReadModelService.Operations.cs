using CoinBot.Application.Abstractions.Administration;
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

        var rows = strategies.Select(strategy =>
        {
            var strategySignals = signals.Where(signal => signal.TradingStrategyId == strategy.Id).ToArray();
            var strategyVetoes = vetoes.Where(veto => veto.TradingStrategyId == strategy.Id).ToArray();
            var latestSignal = strategySignals.OrderByDescending(signal => signal.GeneratedAtUtc).FirstOrDefault();
            var latestVeto = strategyVetoes.OrderByDescending(veto => veto.EvaluatedAtUtc).FirstOrDefault();
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
                strategy.PublishedMode is null ? "Draft / shadow" : $"Published {strategy.PublishedMode}");
        }).ToArray();

        rows = rows
            .Where(row => string.IsNullOrWhiteSpace(normalizedQuery) ||
                          row.StrategyKey.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase) ||
                          row.DisplayName.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase) ||
                          row.LatestSignalType.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var totalSignals = signals.Count;
        var totalVetoes = vetoes.Count;
        var publishedStrategies = strategies.Count(strategy => strategy.PublishedMode.HasValue);
        var lowConfidenceCount = rows.Count(row => row.VetoRate >= 0.20m);
        var summaryTiles = new[]
        {
            new AdminStatTileSnapshot("Strategy", strategies.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), "Tracked strategies", "info"),
            new AdminStatTileSnapshot("Signal", totalSignals.ToString(System.Globalization.CultureInfo.InvariantCulture), "Recent signal volume", "healthy"),
            new AdminStatTileSnapshot("Veto", totalVetoes.ToString(System.Globalization.CultureInfo.InvariantCulture), "Risk guard rejects", totalVetoes > 0 ? "warning" : "neutral"),
            new AdminStatTileSnapshot("Published", publishedStrategies.ToString(System.Globalization.CultureInfo.InvariantCulture), "Live / demo rollout", "info"),
            new AdminStatTileSnapshot("Low confidence", lowConfidenceCount.ToString(System.Globalization.CultureInfo.InvariantCulture), "Veto rate >= 20%", lowConfidenceCount > 0 ? "critical" : "healthy")
        };

        var healthTiles = new[]
        {
            new AdminStatTileSnapshot("Model sağlığı", publishedStrategies > 0 ? "Guarded" : "Draft", publishedStrategies > 0 ? "Live / shadow mix" : "No published template", publishedStrategies > 0 ? "warning" : "neutral"),
            new AdminStatTileSnapshot("Veto yoğunluğu", totalSignals == 0 ? "0%" : $"{Math.Round((decimal)totalVetoes / Math.Max(totalSignals, 1) * 100, 1, MidpointRounding.AwayFromZero)}%", "Veto / signal ratio", totalVetoes > 0 ? "critical" : "healthy"),
            new AdminStatTileSnapshot("Explainability", rows.Any(row => row.LatestSignalType != "-") ? "Available" : "No signal", "Recent signal feed", rows.Any(row => row.LatestSignalType != "-") ? "info" : "neutral"),
            new AdminStatTileSnapshot("Freshness", rows.Any() ? BuildRelativeTimeLabel(now, signals.OrderByDescending(signal => signal.GeneratedAtUtc).FirstOrDefault()?.GeneratedAtUtc) : "n/a", "Latest generated signal", rows.Any() ? "healthy" : "neutral")
        };

        return new AdminStrategyAiMonitoringPageSnapshot(normalizedQuery, summaryTiles, rows, healthTiles, now);
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
        var matchedUsers = users
            .Where(user => MatchesSupportQuery(user, normalizedQuery))
            .Select(user => new AdminUserListItemSnapshot(
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
                0,
                0,
                user.MfaUpdatedAtUtc ?? user.TradingModeApprovedAtUtc,
                BuildRelativeTimeLabel(now, user.MfaUpdatedAtUtc ?? user.TradingModeApprovedAtUtc),
                BuildFreshnessTone(now, user.MfaUpdatedAtUtc ?? user.TradingModeApprovedAtUtc)))
            .Take(6)
            .ToArray();

        var matchedUserIds = matchedUsers.Select(item => item.UserId).ToArray();
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
        var botErrors = matchedBots.Select(bot =>
            new AdminBotOperationSnapshot(
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
                bot.OpenPositionCount > 0 ? "Open exposure" : "No failure",
                bot.OpenOrderCount,
                bot.OpenPositionCount))
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

    private static string BuildTradingModeLabel(ExecutionEnvironment mode) =>
        mode == ExecutionEnvironment.Live ? "Live" : "Demo";

    private static string BuildTradingModeTone(ExecutionEnvironment mode) =>
        mode == ExecutionEnvironment.Live ? "critical" : "neutral";

    private static string BuildStrategyHealthLabel(TradingStrategy strategy, int signalCount, int vetoCount)
    {
        if (strategy.PublishedMode.HasValue)
        {
            return vetoCount > signalCount / 3 ? "Guarded" : "Live";
        }

        return signalCount == 0 ? "Draft" : "Monitoring";
    }

    private static string BuildStrategyHealthTone(TradingStrategy strategy, int signalCount, int vetoCount)
    {
        if (strategy.PublishedMode.HasValue)
        {
            return vetoCount > signalCount / 3 ? "warning" : "healthy";
        }

        return signalCount == 0 ? "neutral" : "info";
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
