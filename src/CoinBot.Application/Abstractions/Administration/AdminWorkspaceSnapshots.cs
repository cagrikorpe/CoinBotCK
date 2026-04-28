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
    string EnvironmentLabel,
    string EnvironmentTone,
    string SyncStatus,
    string SyncStatusTone,
    DateTime? LastValidatedAtUtc,
    string LastValidatedLabel,
    string? LastFailureReason);

public sealed record AdminUserEnvironmentSnapshot(
    string EffectiveEnvironmentLabel,
    string EffectiveEnvironmentTone,
    string ResolutionSourceLabel,
    string ResolutionReason,
    bool HasExplicitLiveApproval);

public sealed record AdminUserRiskOverrideSnapshot(
    string RiskProfileName,
    decimal? MaxDailyLossPercentage,
    decimal? MaxPositionSizePercentage,
    decimal? MaxLeverage,
    bool KillSwitchEnabled,
    bool SessionDisabled,
    bool ReduceOnly,
    decimal? OverrideLeverageCap,
    decimal? OverrideMaxOrderSize,
    int? OverrideMaxDailyTrades,
    string SummaryLabel,
    string SummaryTone,
    string SummaryText);

public sealed record AdminUserExchangeValidationSnapshot(
    string ExchangeAccountId,
    string ExchangeDisplayName,
    DateTime ValidatedAtUtc,
    string TimeLabel,
    string ValidationStatus,
    string ValidationTone,
    bool IsKeyValid,
    bool CanTrade,
    bool CanWithdraw,
    bool SupportsSpot,
    bool SupportsFutures,
    string EnvironmentScope,
    bool IsEnvironmentMatch,
    string PermissionSummary,
    string? FailureReason,
    string? MaskedFingerprint);

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
    AdminUserEnvironmentSnapshot Environment,
    AdminUserRiskOverrideSnapshot RiskOverride,
    IReadOnlyCollection<AdminStatTileSnapshot> SummaryTiles,
    IReadOnlyCollection<AdminUserBotSnapshot> Bots,
    IReadOnlyCollection<AdminUserExchangeSnapshot> ExchangeAccounts,
    IReadOnlyCollection<AdminUserExchangeValidationSnapshot> ValidationHistory,
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
    int OpenPositionCount)
{
    public string? Symbol { get; init; }

    public bool CanManualClose { get; init; }

    public string? ManualCloseSymbol { get; init; }

    public string? ManualCloseExchangeAccountId { get; init; }

    public string? ManualClosePositionQuantityLabel { get; init; }

    public string? ManualClosePositionDirectionLabel { get; init; }

    public string? ManualCloseSideLabel { get; init; }

    public string? ManualCloseEnvironmentLabel { get; init; }

    public string? ManualClosePreviewUnavailableReason { get; init; }

    public string? PositionAdoption { get; init; }

    public string? AdoptedPositionSymbol { get; init; }

    public string? AdoptedPositionQuantity { get; init; }

    public string? AdoptedPositionSide { get; init; }

    public string? AdoptedExchangeAccountId { get; init; }

    public string? AdoptedByBotId { get; init; }

    public string? AdoptionReason { get; init; }

    public bool AutoManagementEnabled { get; init; }
}

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
    string Note,
    string TemplateKey,
    string TemplateName,
    string ValidationStatusCode,
    string ValidationSummary,
    string LatestScoreLabel,
    string LatestExplainabilitySummary,
    string LatestRuleSummary,
    string RuntimeVersionLabel = "n/a",
    string LatestVersionLabel = "n/a",
    string RuntimeTemplateRevisionLabel = "n/a",
    string LatestTemplateRevisionLabel = "n/a",
    string LifecycleTokenLabel = "n/a");

public sealed record AdminStrategyTemplateSnapshot(
    string TemplateKey,
    string TemplateName,
    string Category,
    string ValidationStatusCode,
    string ValidationSummary,
    int SchemaVersion,
    string Description,
    int ActiveRevisionNumber = 1,
    int LatestRevisionNumber = 1,
    int PublishedRevisionNumber = 1,
    string TemplateSource = "BuiltIn",
    string LifecycleStatusLabel = "Active",
    string SourceLineageLabel = "self");

public sealed record AdminTemplateAdoptionSummarySnapshot(
    int TotalTemplateCount,
    int PublishedTemplateCount,
    int ArchivedTemplateCount,
    int TotalCloneCount,
    DateTime? LastCloneAtUtc,
    string LastCloneAtLabel,
    string MostUsedTemplateLabel,
    int ActiveTemplateStrategyCount,
    string LatestValidationIssueSummary);

public sealed record AdminTemplateAdoptionRowSnapshot(
    string TemplateKey,
    string TemplateName,
    int CloneCount,
    int ActiveStrategyCount,
    string LastCloneAtLabel,
    string LifecycleStatusLabel,
    string ValidationStatusCode);

public sealed record AdminRecentTemplateCloneSnapshot(
    string StrategyKey,
    string StrategyDisplayName,
    string TemplateKey,
    string TemplateName,
    string VersionLabel,
    string TemplateRevisionLabel,
    string CreatedAtLabel);

public sealed record AdminStrategyExplainabilitySnapshot(
    string StrategyKey,
    string Symbol,
    string Timeframe,
    string Outcome,
    string ScoreLabel,
    string Summary,
    string RuleSummary,
    string TemplateName,
    DateTime? EvaluatedAtUtc,
    string? TemplateRevisionLabel = null,
    string? VersionLabel = null);

public sealed record AdminStrategyAiMonitoringPageSnapshot(
    string? Query,
    IReadOnlyCollection<AdminStatTileSnapshot> SummaryTiles,
    IReadOnlyCollection<AdminStrategyUsageSnapshot> UsageRows,
    IReadOnlyCollection<AdminStatTileSnapshot> HealthTiles,
    IReadOnlyCollection<AdminStrategyTemplateSnapshot> TemplateCatalog,
    AdminTemplateAdoptionSummarySnapshot TemplateAdoptionSummary,
    IReadOnlyCollection<AdminTemplateAdoptionRowSnapshot> TemplateAdoptionRows,
    IReadOnlyCollection<AdminRecentTemplateCloneSnapshot> RecentTemplateClones,
    AdminStrategyExplainabilitySnapshot LatestExplainability,
    DateTime LastRefreshedAtUtc)
{
    public static AdminStrategyAiMonitoringPageSnapshot Empty(DateTime lastRefreshedAtUtc) =>
        new(
            null,
            Array.Empty<AdminStatTileSnapshot>(),
            Array.Empty<AdminStrategyUsageSnapshot>(),
            Array.Empty<AdminStatTileSnapshot>(),
            Array.Empty<AdminStrategyTemplateSnapshot>(),
            new AdminTemplateAdoptionSummarySnapshot(0, 0, 0, 0, null, "No clone", "n/a", 0, "No validation issue"),
            Array.Empty<AdminTemplateAdoptionRowSnapshot>(),
            Array.Empty<AdminRecentTemplateCloneSnapshot>(),
            new AdminStrategyExplainabilitySnapshot("n/a", "n/a", "n/a", "NotEvaluated", "n/a", "Explainability snapshot yok.", "n/a", "custom", null),
            lastRefreshedAtUtc);
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




