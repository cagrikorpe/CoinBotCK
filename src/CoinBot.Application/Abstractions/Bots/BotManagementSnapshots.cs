namespace CoinBot.Application.Abstractions.Bots;

public sealed record BotManagementPageSnapshot(
    IReadOnlyCollection<BotManagementBotSnapshot> Bots);

public sealed record BotManagementBotSnapshot(
    Guid BotId,
    string Name,
    string StrategyKey,
    string? StrategyDisplayName,
    bool HasPublishedStrategyVersion,
    string Symbol,
    decimal? Quantity,
    Guid? ExchangeAccountId,
    string? ExchangeAccountDisplayName,
    bool ExchangeAccountIsActive,
    bool ExchangeAccountIsWritable,
    decimal? Leverage,
    string? MarginType,
    bool IsEnabled,
    int OpenOrderCount,
    int OpenPositionCount,
    string? LastJobStatus,
    string? LastJobErrorCode,
    string? LastExecutionState,
    string? LastExecutionFailureCode,
    string? LastExecutionBlockDetail,
    DateTime? CooldownBlockedUntilUtc,
    int? CooldownRemainingSeconds,
    DateTime? LastExecutionUpdatedAtUtc,
    DateTime UpdatedAtUtc,
    DateTime? LastExecutionLastCandleAtUtc = null,
    int? LastExecutionDataAgeMilliseconds = null,
    string? LastExecutionContinuityState = null,
    int? LastExecutionContinuityGapCount = null,
    string? LastExecutionStaleReason = null,
    string? LastExecutionAffectedSymbol = null,
    string? LastExecutionAffectedTimeframe = null);

public sealed record BotManagementEditorSnapshot(
    Guid? BotId,
    BotManagementDraftSnapshot Draft,
    IReadOnlyCollection<string> SymbolOptions,
    IReadOnlyCollection<BotStrategyOptionSnapshot> StrategyOptions,
    IReadOnlyCollection<BotExchangeAccountOptionSnapshot> ExchangeAccountOptions);

public sealed record BotManagementDraftSnapshot(
    string Name,
    string StrategyKey,
    string Symbol,
    decimal? Quantity,
    Guid? ExchangeAccountId,
    decimal? Leverage,
    string MarginType,
    bool IsEnabled);

public sealed record BotStrategyOptionSnapshot(
    string StrategyKey,
    string DisplayName,
    bool HasPublishedVersion);

public sealed record BotExchangeAccountOptionSnapshot(
    Guid ExchangeAccountId,
    string DisplayName,
    bool IsActive,
    bool IsWritable);

public sealed record BotManagementSaveCommand(
    string Name,
    string StrategyKey,
    string Symbol,
    decimal? Quantity,
    Guid? ExchangeAccountId,
    decimal? Leverage,
    string MarginType,
    bool IsEnabled);

public sealed record BotManagementSaveResult(
    Guid? BotId,
    bool IsSuccessful,
    bool WasPersisted,
    bool IsEnabled,
    string? FailureCode,
    string? FailureReason);

