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
            .IgnoreQueryFilters()
            .Where(bot => !bot.IsDeleted)
            .GroupBy(bot => bot.OwnerUserId)
            .Select(group => new { UserId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.UserId, item => item.Count, cancellationToken);
        var exchangeCounts = await dbContext.ExchangeAccounts
            .AsNoTracking()
            .IgnoreQueryFilters()
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
        var environment = await LoadUserEnvironmentAsync(normalizedUserId, cancellationToken);
        var riskOverride = await LoadUserRiskOverrideAsync(normalizedUserId, cancellationToken);
        var bots = await LoadUserBotsAsync(user, cancellationToken);
        var exchangeAccounts = await LoadUserExchangeAccountsAsync(normalizedUserId, cancellationToken);
        var validationHistory = await LoadUserExchangeValidationHistoryAsync(normalizedUserId, cancellationToken);
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
            environment,
            riskOverride,
            summaryTiles,
            bots,
            exchangeAccounts,
            validationHistory,
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
            .IgnoreQueryFilters()
            .Where(account => !account.IsDeleted && account.OwnerUserId == userId)
            .OrderByDescending(account => account.UpdatedDate)
            .Take(8)
            .ToListAsync(cancellationToken);
        var exchangeAccountIds = exchangeAccounts.Select(account => account.Id).ToArray();
        var validations = exchangeAccountIds.Length == 0
            ? []
            : await dbContext.ApiCredentialValidations
                .AsNoTracking()
                .Where(entity => entity.OwnerUserId == userId &&
                                 exchangeAccountIds.Contains(entity.ExchangeAccountId) &&
                                 !entity.IsDeleted)
                .OrderByDescending(entity => entity.ValidatedAtUtc)
                .ToListAsync(cancellationToken);
        var latestValidationLookup = validations
            .GroupBy(entity => entity.ExchangeAccountId)
            .ToDictionary(group => group.Key, group => group.First());
        var syncStates = exchangeAccountIds.Length == 0
            ? []
            : await dbContext.ExchangeAccountSyncStates
                .AsNoTracking()
                .IgnoreQueryFilters()
                .Where(entity => exchangeAccountIds.Contains(entity.ExchangeAccountId) && !entity.IsDeleted)
                .OrderByDescending(entity => entity.UpdatedDate)
                .ToListAsync(cancellationToken);
        var syncStateLookup = syncStates
            .GroupBy(entity => entity.ExchangeAccountId)
            .ToDictionary(group => group.Key, group => group.First());
        var utcNow = UtcNow;

        return exchangeAccounts.Select(account =>
        {
            latestValidationLookup.TryGetValue(account.Id, out var latestValidation);
            syncStateLookup.TryGetValue(account.Id, out var syncState);
            var lastValidatedAtUtc = latestValidation?.ValidatedAtUtc ?? account.LastValidatedAt;

            return new AdminUserExchangeSnapshot(
                account.Id.ToString(),
                account.ExchangeName,
                string.IsNullOrWhiteSpace(account.DisplayName) ? account.ExchangeName : account.DisplayName,
                BuildCredentialStatusLabel(account.CredentialStatus),
                BuildCredentialTone(account.CredentialStatus),
                MaskFingerprint(account.CredentialFingerprint),
                latestValidation?.PermissionSummary ?? (account.IsReadOnly ? "Trade=N; Withdraw=?; Spot=?; Futures=?; Env=Unknown" : "Trade=Y; Withdraw=?; Spot=?; Futures=?; Env=Unknown"),
                latestValidation?.EnvironmentScope ?? "Unknown",
                latestValidation?.IsEnvironmentMatch == false ? "critical" : BuildEnvironmentTone(latestValidation?.EnvironmentScope),
                BuildSyncStatusLabel(syncState),
                BuildSyncStatusTone(syncState),
                lastValidatedAtUtc,
                BuildRelativeTimeLabel(utcNow, lastValidatedAtUtc),
                latestValidation?.FailureReason ?? BuildCredentialFailureReason(account.CredentialStatus));
        }).ToArray();
    }

    private async Task<IReadOnlyCollection<AdminUserExchangeValidationSnapshot>> LoadUserExchangeValidationHistoryAsync(
        string userId,
        CancellationToken cancellationToken)
    {
        var exchangeAccounts = await dbContext.ExchangeAccounts
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(entity => entity.OwnerUserId == userId && !entity.IsDeleted)
            .Select(entity => new
            {
                entity.Id,
                entity.ExchangeName,
                entity.DisplayName,
                entity.CredentialFingerprint
            })
            .ToListAsync(cancellationToken);
        var accountLookup = exchangeAccounts.ToDictionary(entity => entity.Id);
        var validations = await dbContext.ApiCredentialValidations
            .AsNoTracking()
            .Where(entity => entity.OwnerUserId == userId && !entity.IsDeleted)
            .OrderByDescending(entity => entity.ValidatedAtUtc)
            .Take(12)
            .ToListAsync(cancellationToken);

        return validations.Select(validation =>
        {
            accountLookup.TryGetValue(validation.ExchangeAccountId, out var account);

            return new AdminUserExchangeValidationSnapshot(
                validation.ExchangeAccountId.ToString(),
                account is null
                    ? "Binance"
                    : string.IsNullOrWhiteSpace(account.DisplayName) ? account.ExchangeName : account.DisplayName,
                validation.ValidatedAtUtc,
                BuildRelativeTimeLabel(UtcNow, validation.ValidatedAtUtc),
                validation.ValidationStatus,
                BuildValidationTone(validation.ValidationStatus),
                validation.IsKeyValid,
                validation.CanTrade,
                validation.CanWithdraw,
                validation.SupportsSpot,
                validation.SupportsFutures,
                validation.EnvironmentScope ?? "Unknown",
                validation.IsEnvironmentMatch,
                validation.PermissionSummary,
                validation.FailureReason,
                account is null ? null : MaskFingerprint(account.CredentialFingerprint));
        }).ToArray();
    }

    private async Task<AdminUserEnvironmentSnapshot> LoadUserEnvironmentAsync(
        string userId,
        CancellationToken cancellationToken)
    {
        var resolution = await tradingModeResolver.ResolveAsync(
            new CoinBot.Application.Abstractions.Execution.TradingModeResolutionRequest(UserId: userId),
            cancellationToken);
        var label = resolution.EffectiveMode == ExecutionEnvironment.Live ? "Live" : "Demo";
        var tone = resolution.EffectiveMode switch
        {
            ExecutionEnvironment.Live when resolution.HasExplicitLiveApproval => "warning",
            ExecutionEnvironment.Live => "critical",
            _ => "info"
        };
        var sourceLabel = resolution.ResolutionSource switch
        {
            CoinBot.Application.Abstractions.Execution.TradingModeResolutionSource.UserOverride => "Kullanıcı override",
            CoinBot.Application.Abstractions.Execution.TradingModeResolutionSource.BotOverride => "Bot override",
            CoinBot.Application.Abstractions.Execution.TradingModeResolutionSource.LiveApprovalGuard => "Live guard",
            CoinBot.Application.Abstractions.Execution.TradingModeResolutionSource.StrategyPromotionGuard => "Strateji guard",
            CoinBot.Application.Abstractions.Execution.TradingModeResolutionSource.ContextGuard => "Kapsam guard",
            _ => "Global varsayılan"
        };

        return new AdminUserEnvironmentSnapshot(
            label,
            tone,
            sourceLabel,
            resolution.Reason,
            resolution.HasExplicitLiveApproval);
    }

    private async Task<AdminUserRiskOverrideSnapshot> LoadUserRiskOverrideAsync(
        string userId,
        CancellationToken cancellationToken)
    {
        var riskProfile = await dbContext.RiskProfiles
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(entity => entity.OwnerUserId == userId && !entity.IsDeleted)
            .OrderByDescending(entity => entity.UpdatedDate)
            .FirstOrDefaultAsync(cancellationToken);
        var executionOverride = await dbContext.UserExecutionOverrides
            .AsNoTracking()
            .Where(entity => entity.UserId == userId && !entity.IsDeleted)
            .OrderByDescending(entity => entity.UpdatedDate)
            .FirstOrDefaultAsync(cancellationToken);
        var summaryLabel = executionOverride?.SessionDisabled == true
            ? "İşlem oturumu kapalı"
            : executionOverride?.ReduceOnly == true
                ? "Reduce-only aktif"
                : riskProfile is null
                    ? "Risk profili eksik"
                    : "Risk ve override hazır";
        var summaryTone = executionOverride?.SessionDisabled == true
            ? "critical"
            : executionOverride?.ReduceOnly == true || riskProfile is null
                ? "warning"
                : "healthy";
        var summaryText = riskProfile is null
            ? "Bu kullanıcı için tanımlı risk profili yok."
            : $"Profil '{riskProfile.ProfileName}' · Günlük kayıp %{riskProfile.MaxDailyLossPercentage:0.##} · Pozisyon %{riskProfile.MaxPositionSizePercentage:0.##} · Maks kaldıraç {riskProfile.MaxLeverage:0.##}x";

        if (executionOverride is not null)
        {
            summaryText = $"{summaryText} · Override: {BuildOverrideSummary(executionOverride)}";
        }

        return new AdminUserRiskOverrideSnapshot(
            riskProfile?.ProfileName ?? "Tanımlı değil",
            riskProfile?.MaxDailyLossPercentage,
            riskProfile?.MaxPositionSizePercentage,
            riskProfile?.MaxLeverage,
            riskProfile?.KillSwitchEnabled ?? false,
            executionOverride?.SessionDisabled ?? false,
            executionOverride?.ReduceOnly ?? false,
            executionOverride?.LeverageCap,
            executionOverride?.MaxOrderSize,
            executionOverride?.MaxDailyTrades,
            summaryLabel,
            summaryTone,
            summaryText);
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
            .IgnoreQueryFilters()
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

    private static string BuildCredentialStatusLabel(ExchangeCredentialStatus status)
    {
        return status switch
        {
            ExchangeCredentialStatus.Active => "Aktif",
            ExchangeCredentialStatus.PendingValidation => "Doğrulama bekliyor",
            ExchangeCredentialStatus.RevalidationRequired => "Yeniden doğrulama gerekli",
            ExchangeCredentialStatus.RotationRequired => "Rotasyon gerekli",
            ExchangeCredentialStatus.Invalid => "Geçersiz",
            _ => "Eksik"
        };
    }

    private static string? BuildCredentialFailureReason(ExchangeCredentialStatus status)
    {
        return status switch
        {
            ExchangeCredentialStatus.PendingValidation => "Son doğrulama henüz tamamlanmadı.",
            ExchangeCredentialStatus.RevalidationRequired => "Credential yeniden doğrulanmalı.",
            ExchangeCredentialStatus.RotationRequired => "Credential rotasyonu gerekli.",
            ExchangeCredentialStatus.Invalid => "Son doğrulama başarısız.",
            ExchangeCredentialStatus.Missing => "Henüz credential kaydı yok.",
            _ => null
        };
    }

    private static string BuildEnvironmentTone(string? environmentScope)
    {
        return string.Equals(environmentScope, "Live", StringComparison.OrdinalIgnoreCase)
            ? "warning"
            : string.Equals(environmentScope, "Demo", StringComparison.OrdinalIgnoreCase)
                ? "info"
                : "neutral";
    }

    private static string BuildSyncStatusLabel(ExchangeAccountSyncState? syncState)
    {
        if (syncState is null)
        {
            return "Henüz senkron yok";
        }

        return syncState.PrivateStreamConnectionState switch
        {
            ExchangePrivateStreamConnectionState.Connected when syncState.DriftStatus == ExchangeStateDriftStatus.InSync => "Bağlı",
            ExchangePrivateStreamConnectionState.Connected => "Bağlı, drift izleniyor",
            ExchangePrivateStreamConnectionState.Reconnecting => "Yeniden bağlanıyor",
            ExchangePrivateStreamConnectionState.Connecting => "Bağlanıyor",
            ExchangePrivateStreamConnectionState.ListenKeyExpired => "Listen key süresi doldu",
            _ => "Bağlı değil"
        };
    }

    private static string BuildSyncStatusTone(ExchangeAccountSyncState? syncState)
    {
        if (syncState is null)
        {
            return "neutral";
        }

        return syncState.PrivateStreamConnectionState switch
        {
            ExchangePrivateStreamConnectionState.Connected when syncState.DriftStatus == ExchangeStateDriftStatus.InSync => "healthy",
            ExchangePrivateStreamConnectionState.Connected => "warning",
            ExchangePrivateStreamConnectionState.Reconnecting => "warning",
            ExchangePrivateStreamConnectionState.Connecting => "info",
            ExchangePrivateStreamConnectionState.ListenKeyExpired => "critical",
            _ => "critical"
        };
    }

    private static string BuildValidationTone(string validationStatus)
    {
        return string.Equals(validationStatus, "Valid", StringComparison.OrdinalIgnoreCase)
            ? "healthy"
            : "critical";
    }

    private static string BuildOverrideSummary(UserExecutionOverride executionOverride)
    {
        var parts = new List<string>();

        if (executionOverride.SessionDisabled)
        {
            parts.Add("oturum kapalı");
        }

        if (executionOverride.ReduceOnly)
        {
            parts.Add("reduce-only");
        }

        if (executionOverride.LeverageCap.HasValue)
        {
            parts.Add($"kaldıraç tavanı {executionOverride.LeverageCap.Value:0.##}x");
        }

        if (executionOverride.MaxOrderSize.HasValue)
        {
            parts.Add($"max emir {executionOverride.MaxOrderSize.Value:0.########}");
        }

        if (executionOverride.MaxDailyTrades.HasValue)
        {
            parts.Add($"günlük işlem {executionOverride.MaxDailyTrades.Value}");
        }

        return parts.Count == 0
            ? "ek override yok"
            : string.Join(", ", parts);
    }

    private static bool ContainsUserReference(string source, string userId)
    {
        return source.Contains(userId, StringComparison.OrdinalIgnoreCase);
    }
}
