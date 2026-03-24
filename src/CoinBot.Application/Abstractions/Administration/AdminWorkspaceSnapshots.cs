namespace CoinBot.Application.Abstractions.Administration;

public sealed record AdminStatTileSnapshot(
    string Label,
    string Value,
    string Meta,
    string Tone = "neutral");

public sealed record AdminUserListItemSnapshot(
    string UserId,
    string DisplayName,
    string UserName,
    string? Email,
    string StatusLabel,
    string StatusTone,
    string MfaLabel,
    string MfaTone,
    string RoleSummary,
    string TradingModeLabel,
    string TradingModeTone,
    int BotCount,
    int ExchangeCount,
    DateTime? LastActivityAtUtc,
    string LastActivityLabel,
    string LastActivityTone);

public sealed record AdminUsersPageSnapshot(
    string? Query,
    string? StatusFilter,
    string? MfaFilter,
    IReadOnlyCollection<AdminStatTileSnapshot> SummaryTiles,
    IReadOnlyCollection<AdminUserListItemSnapshot> Users,
    DateTime LastRefreshedAtUtc)
{
    public static AdminUsersPageSnapshot Empty(DateTime lastRefreshedAtUtc) =>
        new(null, null, null, Array.Empty<AdminStatTileSnapshot>(), Array.Empty<AdminUserListItemSnapshot>(), lastRefreshedAtUtc);
}

public sealed record AdminUserBotSnapshot(
    string BotId,
    string Name,
    string StrategyKey,
    string StatusLabel,
    string StatusTone,
    string ModeLabel,
    string ModeTone,
    int OpenOrderCount,
    int OpenPositionCount,
    string LastAction,
    string LastError);

public sealed record AdminUserExchangeSnapshot(
    string ExchangeAccountId,
    string ExchangeName,
    string DisplayName,
    string CredentialStatus,
    string CredentialStatusTone,
    string Fingerprint,
    string PermissionSummary,
    DateTime? LastValidatedAtUtc,
    string? LastFailureReason);

public sealed record AdminUserActivitySnapshot(
    DateTime TimestampUtc,
    string Title,
    string Summary,
    string Module,
    string TimeLabel,
    string Tone);

public sealed record AdminUserLogSnapshot(
    DateTime TimestampUtc,
    string Title,
    string Module,
    string TimeLabel,
    string Severity,
    string Tone);

public sealed record AdminUserDetailPageSnapshot(
    string UserId,
    string DisplayName,
    string UserName,
    string? Email,
    string RoleSummary,
    string StatusLabel,
    string StatusTone,
    string MfaLabel,
    string MfaTone,
    string TradingModeLabel,
    string TradingModeTone,
    string RiskLabel,
    string RiskTone,
    IReadOnlyCollection<AdminStatTileSnapshot> SummaryTiles,
    IReadOnlyCollection<AdminUserBotSnapshot> Bots,
    IReadOnlyCollection<AdminUserExchangeSnapshot> ExchangeAccounts,
    IReadOnlyCollection<AdminUserActivitySnapshot> Activity,
    IReadOnlyCollection<AdminUserLogSnapshot> CriticalLogs,
    DateTime? LastSecurityEventAtUtc,
    DateTime LastRefreshedAtUtc);

public sealed record AdminBotOperationSnapshot(
    string BotId,
    string Name,
    string OwnerUserId,
    string OwnerDisplayName,
    string StatusLabel,
    string StatusTone,
    string ModeLabel,
    string ModeTone,
    string StrategyKey,
    string RiskLabel,
    string RiskTone,
    string LastAction,
    string LastError,
    int OpenOrderCount,
    int OpenPositionCount);

public sealed record AdminBotOperationsPageSnapshot(
    string? Query,
    string? StatusFilter,
    string? ModeFilter,
    IReadOnlyCollection<AdminStatTileSnapshot> SummaryTiles,
    IReadOnlyCollection<AdminBotOperationSnapshot> Bots,
    DateTime LastRefreshedAtUtc)
{
    public static AdminBotOperationsPageSnapshot Empty(DateTime lastRefreshedAtUtc) =>
        new(null, null, null, Array.Empty<AdminStatTileSnapshot>(), Array.Empty<AdminBotOperationSnapshot>(), lastRefreshedAtUtc);
}

public sealed record AdminStrategyUsageSnapshot(
    string StrategyKey,
    string DisplayName,
    string HealthLabel,
    string HealthTone,
    int SignalCount,
    int VetoCount,
    decimal VetoRate,
    string LatestSignalType,
    string LatestSignalAtLabel,
    string? LatestVetoReason,
    string Note);

public sealed record AdminStrategyAiMonitoringPageSnapshot(
    string? Query,
    IReadOnlyCollection<AdminStatTileSnapshot> SummaryTiles,
    IReadOnlyCollection<AdminStrategyUsageSnapshot> UsageRows,
    IReadOnlyCollection<AdminStatTileSnapshot> HealthTiles,
    DateTime LastRefreshedAtUtc)
{
    public static AdminStrategyAiMonitoringPageSnapshot Empty(DateTime lastRefreshedAtUtc) =>
        new(null, Array.Empty<AdminStatTileSnapshot>(), Array.Empty<AdminStrategyUsageSnapshot>(), Array.Empty<AdminStatTileSnapshot>(), lastRefreshedAtUtc);
}

public sealed record AdminSupportLookupSnapshot(
    string? Query,
    string EmptyStateTitle,
    string EmptyStateMessage,
    IReadOnlyCollection<AdminStatTileSnapshot> SummaryTiles,
    IReadOnlyCollection<AdminUserListItemSnapshot> MatchedUsers,
    IReadOnlyCollection<AdminUserActivitySnapshot> Diagnostics,
    IReadOnlyCollection<AdminUserLogSnapshot> CriticalEvents,
    IReadOnlyCollection<AdminBotOperationSnapshot> BotErrors,
    DateTime LastRefreshedAtUtc)
{
    public static AdminSupportLookupSnapshot Empty(DateTime lastRefreshedAtUtc) =>
        new(
            null,
            "Arama yapın",
            "Kullanıcı, bot, exchange veya güvenlik kaydı bulmak için gerçek lookup sorgusu girin.",
            Array.Empty<AdminStatTileSnapshot>(),
            Array.Empty<AdminUserListItemSnapshot>(),
            Array.Empty<AdminUserActivitySnapshot>(),
            Array.Empty<AdminUserLogSnapshot>(),
            Array.Empty<AdminBotOperationSnapshot>(),
            lastRefreshedAtUtc);
}

public sealed record AdminSecurityEventSnapshot(
    DateTime TimestampUtc,
    string TimeLabel,
    string Category,
    string CategoryTone,
    string Severity,
    string SeverityTone,
    string Actor,
    string Module,
    string Summary,
    string Detail,
    IReadOnlyCollection<string> Tags);

public sealed record AdminSecurityEventsPageSnapshot(
    string? Query,
    string? SeverityFilter,
    string? ModuleFilter,
    IReadOnlyCollection<AdminStatTileSnapshot> SummaryTiles,
    IReadOnlyCollection<AdminSecurityEventSnapshot> Events,
    DateTime LastRefreshedAtUtc)
{
    public static AdminSecurityEventsPageSnapshot Empty(DateTime lastRefreshedAtUtc) =>
        new(null, null, null, Array.Empty<AdminStatTileSnapshot>(), Array.Empty<AdminSecurityEventSnapshot>(), lastRefreshedAtUtc);
}

public sealed record AdminNotificationSnapshot(
    DateTime TimestampUtc,
    string TimeLabel,
    string Title,
    string Category,
    string CategoryTone,
    string Severity,
    string SeverityTone,
    string Source,
    string StateLabel,
    string StateTone,
    string Summary,
    string Scope);

public sealed record AdminNotificationsPageSnapshot(
    string? SeverityFilter,
    string? CategoryFilter,
    IReadOnlyCollection<AdminStatTileSnapshot> SummaryTiles,
    IReadOnlyCollection<AdminNotificationSnapshot> Alerts,
    DateTime LastRefreshedAtUtc)
{
    public static AdminNotificationsPageSnapshot Empty(DateTime lastRefreshedAtUtc) =>
        new(null, null, Array.Empty<AdminStatTileSnapshot>(), Array.Empty<AdminNotificationSnapshot>(), lastRefreshedAtUtc);
}
