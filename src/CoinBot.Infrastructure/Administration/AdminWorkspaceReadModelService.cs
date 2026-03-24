using System.Globalization;
using CoinBot.Application.Abstractions.Administration;
using CoinBot.Application.Abstractions.Monitoring;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Identity;
using CoinBot.Infrastructure.Persistence;

namespace CoinBot.Infrastructure.Administration;

public sealed partial class AdminWorkspaceReadModelService(
    ApplicationDbContext dbContext,
    IAdminMonitoringReadModelService monitoringReadModelService,
    TimeProvider timeProvider) : IAdminWorkspaceReadModelService
{
    private readonly ApplicationDbContext dbContext = dbContext;
    private readonly IAdminMonitoringReadModelService monitoringReadModelService = monitoringReadModelService;
    private readonly TimeProvider timeProvider = timeProvider;

    protected DateTime UtcNow => timeProvider.GetUtcNow().UtcDateTime;

    protected static string NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? string.Empty : normalized;
    }

    protected static string NormalizeRequired(string value, string parameterName)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized)
            ? throw new ArgumentException("The value is required.", parameterName)
            : normalized;
    }

    protected static string BuildRelativeTimeLabel(DateTime utcNow, DateTime? timestamp)
    {
        if (!timestamp.HasValue)
        {
            return "Veri yok";
        }

        var age = utcNow - timestamp.Value;
        if (age < TimeSpan.FromMinutes(1))
        {
            return "az önce";
        }

        if (age < TimeSpan.FromHours(1))
        {
            return $"{Math.Max(1, (int)age.TotalMinutes)} dk önce";
        }

        if (age < TimeSpan.FromDays(1))
        {
            return $"{Math.Max(1, (int)age.TotalHours)} saat önce";
        }

        if (age < TimeSpan.FromDays(2))
        {
            return "dün";
        }

        if (age < TimeSpan.FromDays(7))
        {
            return $"{Math.Max(1, (int)age.TotalDays)} gün önce";
        }

        return timestamp.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    protected static string BuildFreshnessTone(DateTime utcNow, DateTime? timestamp)
    {
        if (!timestamp.HasValue)
        {
            return "neutral";
        }

        var age = utcNow - timestamp.Value;
        if (age < TimeSpan.FromHours(1))
        {
            return "healthy";
        }

        if (age < TimeSpan.FromDays(1))
        {
            return "info";
        }

        if (age < TimeSpan.FromDays(7))
        {
            return "warning";
        }

        return "critical";
    }

    protected static string BuildUserStatusLabel(ApplicationUser user)
    {
        if (user.LockoutEnd is DateTimeOffset lockoutEnd && lockoutEnd.UtcDateTime > DateTime.UtcNow)
        {
            return "Kilitli";
        }

        if (!user.EmailConfirmed)
        {
            return "Doğrulama bekliyor";
        }

        return "Aktif";
    }

    protected static string BuildUserStatusTone(ApplicationUser user)
    {
        if (user.LockoutEnd is DateTimeOffset lockoutEnd && lockoutEnd.UtcDateTime > DateTime.UtcNow)
        {
            return "critical";
        }

        if (!user.EmailConfirmed)
        {
            return "warning";
        }

        return "healthy";
    }

    protected static string BuildMfaLabel(ApplicationUser user) =>
        user.MfaEnabled || user.TotpEnabled || user.EmailOtpEnabled ? "MFA Açık" : "MFA Kapalı";

    protected static string BuildMfaTone(ApplicationUser user) =>
        user.MfaEnabled || user.TotpEnabled || user.EmailOtpEnabled ? "healthy" : "degraded";

    protected static string BuildTradingModeLabel(ApplicationUser user)
    {
        return user.TradingModeOverride switch
        {
            ExecutionEnvironment.Live => "Live override",
            ExecutionEnvironment.Demo => "Demo override",
            _ => "Inherited"
        };
    }

    protected static string BuildTradingModeTone(ApplicationUser user)
    {
        return user.TradingModeOverride switch
        {
            ExecutionEnvironment.Live when user.TradingModeApprovedAtUtc.HasValue => "healthy",
            ExecutionEnvironment.Live => "warning",
            ExecutionEnvironment.Demo => "neutral",
            _ => "info"
        };
    }
}
