using CoinBot.Application.Abstractions.Administration;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace CoinBot.Infrastructure.Administration;

public sealed partial class AdminWorkspaceReadModelService
{
    public async Task<AdminUsersPageSnapshot> GetUsersAsync(
        string? query = null,
        string? status = null,
        string? mfa = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedQuery = NormalizeOptional(query);
        var normalizedStatus = NormalizeOptional(status);
        var normalizedMfa = NormalizeOptional(mfa);
        var now = UtcNow;

        var users = await dbContext.Users
            .AsNoTracking()
            .OrderBy(user => user.UserName)
            .ToListAsync(cancellationToken);

        var roleLookup = await LoadRoleLookupAsync(cancellationToken);
        var botCounts = await dbContext.TradingBots
            .AsNoTracking()
            .Where(bot => !bot.IsDeleted)
            .GroupBy(bot => bot.OwnerUserId)
            .Select(group => new { UserId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.UserId, item => item.Count, cancellationToken);
        var exchangeCounts = await dbContext.ExchangeAccounts
            .AsNoTracking()
            .Where(account => !account.IsDeleted)
            .GroupBy(account => account.OwnerUserId)
            .Select(group => new { UserId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.UserId, item => item.Count, cancellationToken);
        var activityLookup = await LoadUserActivityLookupAsync(users.Select(user => user.Id).ToArray(), cancellationToken);

        var rows = users.Select(user =>
        {
            var displayName = string.IsNullOrWhiteSpace(user.FullName)
                ? user.UserName ?? user.Email ?? user.Id
                : user.FullName;
            var roles = roleLookup.TryGetValue(user.Id, out var roleNames)
                ? roleNames
                : Array.Empty<string>();
            var botCount = botCounts.TryGetValue(user.Id, out var matchedBotCount) ? matchedBotCount : 0;
            var exchangeCount = exchangeCounts.TryGetValue(user.Id, out var matchedExchangeCount) ? matchedExchangeCount : 0;
            var activity = activityLookup.TryGetValue(user.Id, out var activityAtUtc)
                ? activityAtUtc
                : user.MfaUpdatedAtUtc ?? user.TradingModeApprovedAtUtc;

            return new AdminUserListItemSnapshot(
                user.Id,
                displayName,
                user.UserName ?? user.Id,
                user.Email,
                BuildUserStatusLabel(user),
                BuildUserStatusTone(user),
                BuildMfaLabel(user),
                BuildMfaTone(user),
                roles.Count == 0 ? "Role yok" : string.Join(", ", roles),
                BuildTradingModeLabel(user),
                BuildTradingModeTone(user),
                botCount,
                exchangeCount,
                activity,
                BuildRelativeTimeLabel(now, activity),
                BuildFreshnessTone(now, activity));
        }).ToArray();

        rows = rows
            .Where(row => string.IsNullOrWhiteSpace(normalizedQuery) ||
                          row.DisplayName.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase) ||
                          row.UserName.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase) ||
                          (row.Email?.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase) ?? false) ||
                          row.UserId.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase) ||
                          row.RoleSummary.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase))
            .Where(row => string.IsNullOrWhiteSpace(normalizedStatus) ||
                          row.StatusLabel.Contains(normalizedStatus, StringComparison.OrdinalIgnoreCase))
            .Where(row => string.IsNullOrWhiteSpace(normalizedMfa) ||
                          row.MfaLabel.Contains(normalizedMfa, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var summaryTiles = new[]
        {
            new AdminStatTileSnapshot("Toplam kullanıcı", users.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), "Identity users", "info"),
            new AdminStatTileSnapshot("Aktif kullanıcı", rows.Count(row => row.StatusTone == "healthy").ToString(System.Globalization.CultureInfo.InvariantCulture), "Healthy / unlocked", "healthy"),
            new AdminStatTileSnapshot("MFA açık", rows.Count(row => row.MfaTone == "healthy").ToString(System.Globalization.CultureInfo.InvariantCulture), "TOTP / email OTP", "warning"),
            new AdminStatTileSnapshot("Bot bağlı", botCounts.Values.Sum().ToString(System.Globalization.CultureInfo.InvariantCulture), "Trading bots", "degraded"),
            new AdminStatTileSnapshot("Exchange bağlı", exchangeCounts.Values.Sum().ToString(System.Globalization.CultureInfo.InvariantCulture), "Credential-backed accounts", "info")
        };

        return new AdminUsersPageSnapshot(
            normalizedQuery,
            normalizedStatus,
            normalizedMfa,
            summaryTiles,
            rows,
            now);
    }

    public async Task<AdminUserDetailPageSnapshot?> GetUserDetailAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        var normalizedUserId = NormalizeRequired(userId, nameof(userId));
        var now = UtcNow;

        var user = await dbContext.Users
            .AsNoTracking()
            .SingleOrDefaultAsync(entity => entity.Id == normalizedUserId, cancellationToken);

        if (user is null)
        {
            return null;
        }

        var roleNames = await LoadRolesForUserAsync(normalizedUserId, cancellationToken);
        var bots = await LoadUserBotsAsync(user, cancellationToken);
        var exchangeAccounts = await LoadUserExchangeAccountsAsync(normalizedUserId, cancellationToken);
        var activities = await LoadUserActivityFeedAsync(normalizedUserId, cancellationToken);
        var criticalLogs = await LoadUserCriticalLogsAsync(normalizedUserId, cancellationToken);

        var botCount = bots.Count;
        var exchangeCount = exchangeAccounts.Count;
        var activeBotCount = bots.Count(bot => bot.StatusTone == "healthy");
        var validatedExchangeCount = exchangeAccounts.Count(account => account.CredentialStatusTone == "healthy");
        var lastSecurityEvent = activities.Count > 0
            ? activities.Select(item => item.TimestampUtc).Max()
            : user.MfaUpdatedAtUtc ?? user.TradingModeApprovedAtUtc;

        var summaryTiles = new[]
        {
            new AdminStatTileSnapshot("Rol", roleNames.Count == 0 ? "Yok" : roleNames.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), roleNames.Count == 0 ? "Role assigned değil" : string.Join(", ", roleNames), "info"),
            new AdminStatTileSnapshot("Bot", botCount.ToString(System.Globalization.CultureInfo.InvariantCulture), $"{activeBotCount} aktif", botCount > 0 ? "healthy" : "neutral"),
            new AdminStatTileSnapshot("Exchange", exchangeCount.ToString(System.Globalization.CultureInfo.InvariantCulture), $"{validatedExchangeCount} doğrulanmış", exchangeCount > 0 ? "warning" : "neutral"),
            new AdminStatTileSnapshot("MFA", BuildMfaLabel(user), BuildMfaMeta(user), BuildMfaTone(user)),
            new AdminStatTileSnapshot("Güvenlik", lastSecurityEvent is null ? "Yok" : BuildRelativeTimeLabel(now, lastSecurityEvent), "Son güvenlik güncellemesi", BuildFreshnessTone(now, lastSecurityEvent))
        };

        return new AdminUserDetailPageSnapshot(
            user.Id,
            string.IsNullOrWhiteSpace(user.FullName) ? user.UserName ?? user.Email ?? user.Id : user.FullName,
            user.UserName ?? user.Id,
            user.Email,
            roleNames.Count == 0 ? "Role yok" : string.Join(", ", roleNames),
            BuildUserStatusLabel(user),
            BuildUserStatusTone(user),
            BuildMfaLabel(user),
            BuildMfaTone(user),
            BuildTradingModeLabel(user),
            BuildTradingModeTone(user),
            BuildRiskLabel(botCount, exchangeCount, user),
            BuildRiskTone(botCount, exchangeCount, user),
            summaryTiles,
            bots,
            exchangeAccounts,
            activities,
            criticalLogs,
            lastSecurityEvent,
            now);
    }

    private async Task<Dictionary<string, IReadOnlyCollection<string>>> LoadRoleLookupAsync(CancellationToken cancellationToken)
    {
        return await (from userRole in dbContext.Set<IdentityUserRole<string>>().AsNoTracking()
                      join role in dbContext.Set<IdentityRole>().AsNoTracking()
                          on userRole.RoleId equals role.Id
                      group role.Name by userRole.UserId into grouped
                      select new
                      {
                          UserId = grouped.Key,
                          Roles = grouped.Where(name => name != null).Select(name => name!).ToArray()
                      })
            .ToDictionaryAsync(item => item.UserId, item => (IReadOnlyCollection<string>)item.Roles, cancellationToken);
    }

    private async Task<IReadOnlyCollection<string>> LoadRolesForUserAsync(
        string userId,
        CancellationToken cancellationToken)
    {
        return await (from userRole in dbContext.Set<IdentityUserRole<string>>().AsNoTracking()
                      join role in dbContext.Set<IdentityRole>().AsNoTracking()
                          on userRole.RoleId equals role.Id
                      where userRole.UserId == userId
                      orderby role.Name
                      select role.Name)
            .Where(name => name != null)
            .Select(name => name!)
            .ToArrayAsync(cancellationToken);
    }

    private async Task<Dictionary<string, DateTime?>> LoadUserActivityLookupAsync(
        IReadOnlyCollection<string> userIds,
        CancellationToken cancellationToken)
    {
        if (userIds.Count == 0)
        {
            return new Dictionary<string, DateTime?>(StringComparer.Ordinal);
        }

        var auditLogs = await dbContext.AuditLogs
            .AsNoTracking()
            .Where(log => !log.IsDeleted)
            .OrderByDescending(log => log.CreatedDate)
            .Take(250)
            .Select(log => new { log.Target, log.Actor, CreatedAtUtc = log.CreatedDate })
            .ToListAsync(cancellationToken);

        var adminLogs = await dbContext.AdminAuditLogs
            .AsNoTracking()
            .Where(log => userIds.Contains(log.ActorUserId) || (log.TargetId != null && userIds.Contains(log.TargetId)))
            .OrderByDescending(log => log.CreatedAtUtc)
            .Take(250)
            .Select(log => new { UserId = log.ActorUserId, log.TargetId, log.CreatedAtUtc })
            .ToListAsync(cancellationToken);

        var lookup = new Dictionary<string, DateTime?>(StringComparer.Ordinal);

        foreach (var userId in userIds)
        {
            var latestAudit = auditLogs
                .Where(log => ContainsUserReference(log.Target, userId) || ContainsUserReference(log.Actor, userId))
                .Select(log => log.CreatedAtUtc)
                .Concat(adminLogs.Where(log => log.UserId == userId || log.TargetId == userId).Select(log => log.CreatedAtUtc))
                .OrderByDescending(date => date)
                .FirstOrDefault();

            lookup[userId] = latestAudit == default ? null : latestAudit;
        }

        return lookup;
    }

    private async Task<IReadOnlyCollection<AdminUserExchangeSnapshot>> LoadUserExchangeAccountsAsync(
        string userId,
        CancellationToken cancellationToken)
    {
        var exchangeAccounts = await dbContext.ExchangeAccounts
            .AsNoTracking()
            .Where(account => !account.IsDeleted && account.OwnerUserId == userId)
            .OrderByDescending(account => account.UpdatedDate)
            .Take(8)
            .ToListAsync(cancellationToken);

        return exchangeAccounts.Select(account =>
        {
            var statusLabel = account.CredentialStatus switch
            {
                ExchangeCredentialStatus.Active => "Aktif",
                ExchangeCredentialStatus.PendingValidation => "Doğrulama bekliyor",
                ExchangeCredentialStatus.RevalidationRequired => "Yeniden doğrulama",
                ExchangeCredentialStatus.RotationRequired => "Rotasyon gerekli",
                ExchangeCredentialStatus.Invalid => "Geçersiz",
                _ => "Eksik"
            };

            var failureReason = account.CredentialStatus switch
            {
                ExchangeCredentialStatus.Active => null,
                ExchangeCredentialStatus.PendingValidation => "Validation pending.",
                ExchangeCredentialStatus.RevalidationRequired => "Revalidation required.",
                ExchangeCredentialStatus.RotationRequired => "Rotation required.",
                ExchangeCredentialStatus.Invalid => "Credential validation failed.",
                _ => "Credential missing."
            };

            return new AdminUserExchangeSnapshot(
                account.Id.ToString(),
                account.ExchangeName,
                string.IsNullOrWhiteSpace(account.DisplayName) ? account.ExchangeName : account.DisplayName,
                statusLabel,
                BuildCredentialTone(account.CredentialStatus),
                MaskFingerprint(account.CredentialFingerprint),
                account.IsReadOnly ? "Read-only" : "Trading enabled",
                account.LastValidatedAt,
                failureReason);
        }).ToArray();
    }

    private async Task<IReadOnlyCollection<AdminUserActivitySnapshot>> LoadUserActivityFeedAsync(
        string userId,
        CancellationToken cancellationToken)
    {
        var auditLogs = await dbContext.AuditLogs
            .AsNoTracking()
            .Where(log => !log.IsDeleted && (log.Target.Contains(userId) || log.Actor.Contains(userId)))
            .OrderByDescending(log => log.CreatedDate)
            .Take(8)
            .ToListAsync(cancellationToken);

        var adminLogs = await dbContext.AdminAuditLogs
            .AsNoTracking()
            .Where(log => log.ActorUserId == userId || log.TargetId == userId)
            .OrderByDescending(log => log.CreatedAtUtc)
            .Take(8)
            .ToListAsync(cancellationToken);

        return auditLogs
            .Select(log => new AdminUserActivitySnapshot(
                log.CreatedDate,
                log.Action,
                BuildAuditSummary(log.Action, log.Target, log.Outcome),
                "Audit",
                BuildRelativeTimeLabel(UtcNow, log.CreatedDate),
                BuildEventTone(log.Outcome)))
            .Concat(adminLogs.Select(log => new AdminUserActivitySnapshot(
                log.CreatedAtUtc,
                log.ActionType,
                BuildAuditSummary(log.ActionType, log.TargetType, log.Reason),
                "AdminAudit",
                BuildRelativeTimeLabel(UtcNow, log.CreatedAtUtc),
                log.ActionType.Contains("Failed", StringComparison.OrdinalIgnoreCase) ? "critical" : "warning")))
            .OrderByDescending(item => item.TimestampUtc)
            .Take(8)
            .ToArray();
    }

    private async Task<IReadOnlyCollection<AdminUserLogSnapshot>> LoadUserCriticalLogsAsync(
        string userId,
        CancellationToken cancellationToken)
    {
        var auditLogs = await dbContext.AuditLogs
            .AsNoTracking()
            .Where(log => !log.IsDeleted && (log.Target.Contains(userId) || log.Actor.Contains(userId)))
            .OrderByDescending(log => log.CreatedDate)
            .Take(5)
            .ToListAsync(cancellationToken);

        return auditLogs.Select(log => new AdminUserLogSnapshot(
            log.CreatedDate,
            log.Action,
            log.Target,
            BuildRelativeTimeLabel(UtcNow, log.CreatedDate),
            log.Outcome,
            BuildEventTone(log.Outcome))).ToArray();
    }

    private async Task<IReadOnlyCollection<AdminUserBotSnapshot>> LoadUserBotsAsync(
        ApplicationUser user,
        CancellationToken cancellationToken)
    {
        var bots = await dbContext.TradingBots
            .AsNoTracking()
            .Where(bot => !bot.IsDeleted && bot.OwnerUserId == user.Id)
            .OrderByDescending(bot => bot.UpdatedDate)
            .Take(8)
            .ToListAsync(cancellationToken);

        var latestFailures = await dbContext.ExecutionOrders
            .AsNoTracking()
            .Where(order => !order.IsDeleted && order.FailureCode != null && order.OwnerUserId == user.Id)
            .OrderByDescending(order => order.UpdatedDate)
            .Select(order => new { order.BotId, order.FailureCode, order.FailureDetail })
            .ToListAsync(cancellationToken);

        return bots.Select(bot =>
        {
            var failure = latestFailures.FirstOrDefault(item => item.BotId == bot.Id) is { } latestFailure
                ? string.IsNullOrWhiteSpace(latestFailure.FailureDetail)
                    ? latestFailure.FailureCode ?? "No failure"
                    : $"{latestFailure.FailureCode}: {latestFailure.FailureDetail}"
                : "No failure";

            return new AdminUserBotSnapshot(
                bot.Id.ToString(),
                bot.Name,
                bot.StrategyKey,
                bot.IsEnabled ? "Aktif" : "Pasif",
                bot.IsEnabled ? "healthy" : "neutral",
                BuildTradingModeLabel(bot.TradingModeOverride ?? user.TradingModeOverride ?? ExecutionEnvironment.Demo),
                BuildTradingModeTone(bot.TradingModeOverride ?? user.TradingModeOverride ?? ExecutionEnvironment.Demo),
                bot.OpenOrderCount,
                bot.OpenPositionCount,
                bot.IsEnabled ? "Çalışıyor" : "Durduruldu",
                failure);
        }).ToArray();
    }

    private static string BuildRiskLabel(int botCount, int exchangeCount, ApplicationUser user)
    {
        if (user.LockoutEnd is DateTimeOffset lockoutEnd && lockoutEnd.UtcDateTime > DateTime.UtcNow)
        {
            return "Risk dikkat";
        }

        if (exchangeCount == 0)
        {
            return "Exchange yok";
        }

        return botCount > 0 ? "Risk normal" : "Risk düşük";
    }

    private static string BuildRiskTone(int botCount, int exchangeCount, ApplicationUser user)
    {
        if (user.LockoutEnd is DateTimeOffset lockoutEnd && lockoutEnd.UtcDateTime > DateTime.UtcNow)
        {
            return "critical";
        }

        if (exchangeCount == 0)
        {
            return "warning";
        }

        return botCount > 0 ? "info" : "healthy";
    }

    private static string BuildMfaMeta(ApplicationUser user)
    {
        return user.PreferredMfaProvider is null ? "MFA provider yok" : $"Provider: {user.PreferredMfaProvider}";
    }

    private static bool ContainsUserReference(string source, string userId)
    {
        return source.Contains(userId, StringComparison.OrdinalIgnoreCase);
    }
}
