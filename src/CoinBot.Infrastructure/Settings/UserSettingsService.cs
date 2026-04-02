using CoinBot.Application.Abstractions.Exchange;
using CoinBot.Application.Abstractions.Settings;
using CoinBot.Infrastructure.Identity;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CoinBot.Infrastructure.Settings;

public sealed class UserSettingsService(
    ApplicationDbContext dbContext,
    IBinanceTimeSyncService binanceTimeSyncService) : IUserSettingsService
{
    public async Task<UserSettingsSnapshot?> GetAsync(string userId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        var user = await dbContext.Users
            .AsNoTracking()
            .SingleOrDefaultAsync(entity => entity.Id == userId, cancellationToken);

        if (user is null)
        {
            return null;
        }

        var timeZone = ResolveTimeZone(user.PreferredTimeZoneId);
        var clockSnapshot = await binanceTimeSyncService.GetSnapshotAsync(cancellationToken: cancellationToken);

        return new UserSettingsSnapshot(
            timeZone.Id,
            timeZone.DisplayName,
            ResolveJavaScriptTimeZoneId(timeZone),
            BuildTimeZoneOptions(),
            clockSnapshot);
    }

    public async Task<UserSettingsSaveResult> SaveAsync(
        string userId,
        UserSettingsSaveCommand command,
        string actor,
        string? correlationId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        ArgumentNullException.ThrowIfNull(command);

        var user = await dbContext.Users
            .SingleOrDefaultAsync(entity => entity.Id == userId, cancellationToken);

        if (user is null)
        {
            return new UserSettingsSaveResult(false, "UTC", TimeZoneInfo.Utc.DisplayName, "UserNotFound", "Kullanıcı bulunamadı.");
        }

        if (!TryResolveTimeZone(command.PreferredTimeZoneId, out var timeZone))
        {
            return new UserSettingsSaveResult(
                false,
                "UTC",
                TimeZoneInfo.Utc.DisplayName,
                "TimeZoneInvalid",
                "Seçilen saat dilimi sistem tarafından tanınmadı.");
        }

        user.PreferredTimeZoneId = timeZone.Id;
        await dbContext.SaveChangesAsync(cancellationToken);

        return new UserSettingsSaveResult(true, timeZone.Id, timeZone.DisplayName, null, null);
    }

    private static IReadOnlyCollection<UserTimeZoneOptionSnapshot> BuildTimeZoneOptions()
    {
        return TimeZoneInfo.GetSystemTimeZones()
            .Select(timeZone => new UserTimeZoneOptionSnapshot(
                timeZone.Id,
                $"UTC{FormatOffset(timeZone.BaseUtcOffset)} · {timeZone.DisplayName}"))
            .OrderBy(option => option.DisplayName, StringComparer.Ordinal)
            .ToArray();
    }

    private static TimeZoneInfo ResolveTimeZone(string? preferredTimeZoneId)
    {
        return TryResolveTimeZone(preferredTimeZoneId, out var timeZone)
            ? timeZone
            : TimeZoneInfo.Utc;
    }

    private static bool TryResolveTimeZone(string? preferredTimeZoneId, out TimeZoneInfo timeZone)
    {
        if (!string.IsNullOrWhiteSpace(preferredTimeZoneId))
        {
            try
            {
                timeZone = TimeZoneInfo.FindSystemTimeZoneById(preferredTimeZoneId.Trim());
                return true;
            }
            catch (TimeZoneNotFoundException)
            {
            }
            catch (InvalidTimeZoneException)
            {
            }
        }

        timeZone = TimeZoneInfo.Utc;
        return false;
    }

    private static string ResolveJavaScriptTimeZoneId(TimeZoneInfo timeZone)
    {
        if (TimeZoneInfo.TryConvertWindowsIdToIanaId(timeZone.Id, out var ianaId) &&
            !string.IsNullOrWhiteSpace(ianaId))
        {
            return ianaId;
        }

        return string.Equals(timeZone.Id, "UTC", StringComparison.OrdinalIgnoreCase)
            ? "UTC"
            : "UTC";
    }

    private static string FormatOffset(TimeSpan offset)
    {
        var sign = offset < TimeSpan.Zero ? "-" : "+";
        var absoluteOffset = offset.Duration();
        return $"{sign}{absoluteOffset:hh\\:mm}";
    }
}
