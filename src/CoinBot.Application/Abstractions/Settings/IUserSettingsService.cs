using CoinBot.Application.Abstractions.Exchange;

namespace CoinBot.Application.Abstractions.Settings;

public interface IUserSettingsService
{
    Task<UserSettingsSnapshot?> GetAsync(string userId, CancellationToken cancellationToken = default);

    Task<UserSettingsSaveResult> SaveAsync(
        string userId,
        UserSettingsSaveCommand command,
        string actor,
        string? correlationId = null,
        CancellationToken cancellationToken = default);
}

public sealed record UserSettingsSnapshot(
    string PreferredTimeZoneId,
    string PreferredTimeZoneDisplayName,
    string PreferredTimeZoneJavaScriptId,
    IReadOnlyCollection<UserTimeZoneOptionSnapshot> TimeZoneOptions,
    BinanceTimeSyncSnapshot BinanceTimeSync);

public sealed record UserTimeZoneOptionSnapshot(
    string TimeZoneId,
    string DisplayName);

public sealed record UserSettingsSaveCommand(
    string PreferredTimeZoneId);

public sealed record UserSettingsSaveResult(
    bool IsSuccessful,
    string PreferredTimeZoneId,
    string PreferredTimeZoneDisplayName,
    string? FailureCode,
    string? FailureReason);
