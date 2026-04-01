using CoinBot.Application.Abstractions.ExchangeCredentials;
using CoinBot.Application.Abstractions.Execution;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Administration;
using CoinBot.Infrastructure.Exchange;
using CoinBot.Infrastructure.Identity;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using System.Net.Http;
using System.Text.Json;

namespace CoinBot.Infrastructure.Credentials;

public sealed class UserExchangeCommandCenterService(
    ApplicationDbContext dbContext,
    IExchangeCredentialService exchangeCredentialService,
    ITradingModeResolver tradingModeResolver,
    IBinanceCredentialProbeClient binanceCredentialProbeClient,
    TimeProvider timeProvider) : IUserExchangeCommandCenterService
{
    public async Task<UserExchangeCommandCenterSnapshot> GetSnapshotAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        var normalizedUserId = NormalizeRequired(userId, nameof(userId));
        var user = await dbContext.Users
            .AsNoTracking()
            .SingleOrDefaultAsync(entity => entity.Id == normalizedUserId, cancellationToken)
            ?? throw new InvalidOperationException($"User '{normalizedUserId}' was not found.");
        var resolution = await tradingModeResolver.ResolveAsync(
            new TradingModeResolutionRequest(UserId: normalizedUserId),
            cancellationToken);
        var riskProfile = await dbContext.RiskProfiles
            .AsNoTracking()
            .Where(entity => entity.OwnerUserId == normalizedUserId && !entity.IsDeleted)
            .OrderByDescending(entity => entity.UpdatedDate)
            .FirstOrDefaultAsync(cancellationToken);
        var executionOverride = await dbContext.UserExecutionOverrides
            .AsNoTracking()
            .Where(entity => entity.UserId == normalizedUserId && !entity.IsDeleted)
            .OrderByDescending(entity => entity.UpdatedDate)
            .FirstOrDefaultAsync(cancellationToken);
        var exchangeAccounts = await dbContext.ExchangeAccounts
            .AsNoTracking()
            .Where(entity => entity.OwnerUserId == normalizedUserId &&
                             !entity.IsDeleted &&
                             entity.ExchangeName == BinanceExchangeName)
            .OrderByDescending(entity => entity.UpdatedDate)
            .Take(12)
            .ToListAsync(cancellationToken);
        var exchangeAccountIds = exchangeAccounts.Select(entity => entity.Id).ToArray();
        var validationHistory = exchangeAccountIds.Length == 0
            ? []
            : await dbContext.ApiCredentialValidations
                .AsNoTracking()
                .Where(entity => entity.OwnerUserId == normalizedUserId &&
                                 exchangeAccountIds.Contains(entity.ExchangeAccountId) &&
                                 !entity.IsDeleted)
                .OrderByDescending(entity => entity.ValidatedAtUtc)
                .Take(20)
                .ToListAsync(cancellationToken);
        var latestValidationLookup = validationHistory
            .GroupBy(entity => entity.ExchangeAccountId)
            .ToDictionary(group => group.Key, group => group.First());
        var syncStates = exchangeAccountIds.Length == 0
            ? []
            : await dbContext.ExchangeAccountSyncStates
                .AsNoTracking()
                .Where(entity => exchangeAccountIds.Contains(entity.ExchangeAccountId) && !entity.IsDeleted)
                .OrderByDescending(entity => entity.UpdatedDate)
                .ToListAsync(cancellationToken);
        var syncStateLookup = syncStates
            .GroupBy(entity => entity.ExchangeAccountId)
            .ToDictionary(group => group.Key, group => group.First());
        var accountLookup = exchangeAccounts.ToDictionary(entity => entity.Id);
        var utcNow = timeProvider.GetUtcNow().UtcDateTime;
        var environmentSummary = BuildEnvironmentSummary(resolution);
        var riskOverrideSummary = BuildRiskOverrideSummary(riskProfile, executionOverride);
        var accountSummaries = exchangeAccounts
            .Select(account =>
            {
                latestValidationLookup.TryGetValue(account.Id, out var latestValidation);
                syncStateLookup.TryGetValue(account.Id, out var syncState);
                return MapAccountSummary(account, latestValidation, syncState, environmentSummary, utcNow);
            })
            .ToArray();
        var historyEntries = validationHistory
            .Select(validation =>
            {
                accountLookup.TryGetValue(validation.ExchangeAccountId, out var account);
                return MapValidationHistoryEntry(validation, account, utcNow);
            })
            .ToArray();

        return new UserExchangeCommandCenterSnapshot(
            normalizedUserId,
            ResolveDisplayName(user),
            environmentSummary,
            riskOverrideSummary,
            accountSummaries,
            historyEntries,
            utcNow);
    }

    public async Task<ConnectUserBinanceCredentialResult> ConnectBinanceAsync(
        ConnectUserBinanceCredentialRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var normalizedUserId = NormalizeRequired(request.UserId, nameof(request.UserId));
        var normalizedApiKey = NormalizeRequired(request.ApiKey, nameof(request.ApiKey));
        var normalizedApiSecret = NormalizeRequired(request.ApiSecret, nameof(request.ApiSecret));
        var normalizedActor = NormalizeRequired(request.Actor, nameof(request.Actor));
        var resolution = await tradingModeResolver.ResolveAsync(
            new TradingModeResolutionRequest(UserId: normalizedUserId),
            cancellationToken);
        var exchangeAccount = await GetOrCreateBinanceAccountAsync(
            normalizedUserId,
            request.ExchangeAccountId,
            cancellationToken);

        await exchangeCredentialService.StoreAsync(
            new StoreExchangeCredentialsRequest(
                exchangeAccount.Id,
                normalizedApiKey,
                normalizedApiSecret,
                normalizedActor,
                request.CorrelationId),
            cancellationToken);

        var probeUnavailableReason = default(string);
        BinanceCredentialProbeSnapshot probe;

        try
        {
            probe = await binanceCredentialProbeClient.ProbeAsync(
                normalizedApiKey,
                normalizedApiSecret,
                cancellationToken);
        }
        catch (HttpRequestException)
        {
            probe = CreateUnavailableProbeSnapshot();
            probeUnavailableReason = "Binance doğrulama servisine şu an ulaşılamadı. Anahtar kaydedildi ancak doğrulama tamamlanamadı.";
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            probe = CreateUnavailableProbeSnapshot();
            probeUnavailableReason = "Binance doğrulama isteği zaman aşımına uğradı. Anahtar kaydedildi ancak doğrulama tamamlanamadı.";
        }
        catch (JsonException)
        {
            probe = CreateUnavailableProbeSnapshot();
            probeUnavailableReason = "Binance doğrulama yanıtı işlenemedi. Anahtar kaydedildi ancak doğrulama tamamlanamadı.";
        }

        var effectiveEnvironment = resolution.EffectiveMode;
        var requestedEnvironmentAllowed = request.RequestedEnvironment == effectiveEnvironment;
        var providerEnvironmentScope = string.IsNullOrWhiteSpace(probeUnavailableReason)
            ? ResolveProviderEnvironmentScope(
                request.RequestedTradeMode,
                probe.SpotEnvironmentScope,
                probe.FuturesEnvironmentScope)
            : "Unknown";
        var providerEnvironmentMatch = string.IsNullOrWhiteSpace(probeUnavailableReason) &&
                                       IsProviderEnvironmentMatch(
                                           request.RequestedTradeMode,
                                           request.RequestedEnvironment,
                                           probe.SpotEnvironmentScope,
                                           probe.FuturesEnvironmentScope);
        var environmentMatch = requestedEnvironmentAllowed && providerEnvironmentMatch;
        var demoWithdrawUnknownAccepted = string.IsNullOrWhiteSpace(probeUnavailableReason) &&
                                          request.RequestedEnvironment == ExecutionEnvironment.Demo &&
                                          effectiveEnvironment == ExecutionEnvironment.Demo &&
                                          providerEnvironmentMatch &&
                                          probe.IsKeyValid &&
                                          !probe.HasTimestampSkew &&
                                          !probe.HasIpRestrictionIssue &&
                                          !probe.CanWithdraw.HasValue;
        var withdrawVerified = string.IsNullOrWhiteSpace(probeUnavailableReason) && probe.CanWithdraw.HasValue;
        var canWithdraw = string.IsNullOrWhiteSpace(probeUnavailableReason)
            ? probe.CanWithdraw ?? (demoWithdrawUnknownAccepted ? false : true)
            : true;
        var canTrade = string.IsNullOrWhiteSpace(probeUnavailableReason) &&
                       request.RequestedTradeMode switch
                       {
                           ExchangeTradeModeSelection.Spot => probe.SupportsSpot && probe.CanTrade,
                           ExchangeTradeModeSelection.Futures => probe.SupportsFutures,
                           ExchangeTradeModeSelection.Both => probe.SupportsSpot && probe.CanTrade && probe.SupportsFutures,
                           _ => false
                       };
        withdrawVerified = withdrawVerified || demoWithdrawUnknownAccepted;
        var isKeyValid = string.IsNullOrWhiteSpace(probeUnavailableReason) &&
                         probe.IsKeyValid &&
                         !probe.HasTimestampSkew &&
                         !probe.HasIpRestrictionIssue &&
                         withdrawVerified;
        var failureReason = probeUnavailableReason ?? ResolveFailureReason(
            probe,
            request.RequestedTradeMode,
            request.RequestedEnvironment,
            effectiveEnvironment,
            requestedEnvironmentAllowed,
            providerEnvironmentMatch,
            withdrawVerified,
            canWithdraw,
            canTrade);
        var isValid = string.IsNullOrWhiteSpace(failureReason) &&
                      isKeyValid &&
                      canTrade &&
                      !canWithdraw &&
                      environmentMatch;

        var state = await exchangeCredentialService.SetValidationStateAsync(
            new SetExchangeCredentialValidationStateRequest(
                exchangeAccount.Id,
                isValid,
                normalizedActor,
                request.CorrelationId,
                IsKeyValid: isKeyValid,
                CanTrade: canTrade,
                CanWithdraw: canWithdraw,
                SupportsSpot: probe.SupportsSpot,
                SupportsFutures: probe.SupportsFutures,
                EnvironmentScope: providerEnvironmentScope,
                IsEnvironmentMatch: environmentMatch,
                HasTimestampSkew: probe.HasTimestampSkew,
                HasIpRestrictionIssue: probe.HasIpRestrictionIssue,
                FailureReason: failureReason,
                PermissionSummary: string.IsNullOrWhiteSpace(probeUnavailableReason)
                    ? BuildEffectivePermissionSummary(probe.PermissionSummary, demoWithdrawUnknownAccepted)
                    : "Trade=?; Withdraw=?; Spot=?; Futures=?; Env=Unknown"),
            cancellationToken);

        exchangeAccount.IsReadOnly = !canTrade;
        exchangeAccount.DisplayName = string.IsNullOrWhiteSpace(exchangeAccount.DisplayName)
            ? BinanceExchangeName
            : exchangeAccount.DisplayName.Trim();
        await dbContext.SaveChangesAsync(cancellationToken);

        return new ConnectUserBinanceCredentialResult(
            exchangeAccount.Id,
            isValid,
            BuildCredentialStatusLabel(state.Status),
            BuildCredentialStatusTone(state.Status),
            isValid
                ? "Binance API anahtarı kaydedildi ve doğrulandı."
                : failureReason ?? "Binance API anahtarı doğrulanamadı.",
            failureReason,
            probe.PermissionSummary,
            providerEnvironmentScope);
    }

    private static BinanceCredentialProbeSnapshot CreateUnavailableProbeSnapshot()
    {
        return new BinanceCredentialProbeSnapshot(
            IsKeyValid: false,
            CanTrade: false,
            CanWithdraw: null,
            SupportsSpot: false,
            SupportsFutures: false,
            HasTimestampSkew: false,
            HasIpRestrictionIssue: false,
            SpotEnvironmentScope: "Unknown",
            FuturesEnvironmentScope: "Unknown",
            PermissionSummary: "Trade=?; Withdraw=?; Spot=?; Futures=?; Env=Unknown",
            SafeFailureReason: "Binance doğrulama servisine ulaşılamadı.");
    }

    private async Task<ExchangeAccount> GetOrCreateBinanceAccountAsync(
        string userId,
        Guid? exchangeAccountId,
        CancellationToken cancellationToken)
    {
        if (exchangeAccountId.HasValue)
        {
            var existingAccount = await dbContext.ExchangeAccounts
                .SingleOrDefaultAsync(
                    entity => entity.Id == exchangeAccountId.Value &&
                              entity.OwnerUserId == userId &&
                              !entity.IsDeleted,
                    cancellationToken)
                ?? throw new InvalidOperationException($"Exchange account '{exchangeAccountId.Value}' was not found.");

            if (!string.Equals(existingAccount.ExchangeName, BinanceExchangeName, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Only Binance credential connections are supported on this screen.");
            }

            return existingAccount;
        }

        var account = await dbContext.ExchangeAccounts
            .OrderByDescending(entity => entity.UpdatedDate)
            .FirstOrDefaultAsync(
                entity => entity.OwnerUserId == userId &&
                          entity.ExchangeName == BinanceExchangeName &&
                          !entity.IsDeleted,
                cancellationToken);

        if (account is not null)
        {
            return account;
        }

        account = new ExchangeAccount
        {
            OwnerUserId = userId,
            ExchangeName = BinanceExchangeName,
            DisplayName = BinanceExchangeName,
            IsReadOnly = true,
            CredentialStatus = ExchangeCredentialStatus.Missing
        };

        dbContext.ExchangeAccounts.Add(account);
        await dbContext.SaveChangesAsync(cancellationToken);
        return account;
    }

    private static UserExchangeEnvironmentSummary BuildEnvironmentSummary(TradingModeResolution resolution)
    {
        var effectiveEnvironmentLabel = resolution.EffectiveMode == ExecutionEnvironment.Live ? "Live" : "Demo";
        var effectiveEnvironmentTone = resolution.EffectiveMode switch
        {
            ExecutionEnvironment.Live when resolution.HasExplicitLiveApproval => "warning",
            ExecutionEnvironment.Live => "critical",
            _ => "info"
        };
        var resolutionSourceLabel = resolution.ResolutionSource switch
        {
            TradingModeResolutionSource.UserOverride => "Kullanıcı override",
            TradingModeResolutionSource.BotOverride => "Bot override",
            TradingModeResolutionSource.LiveApprovalGuard => "Live guard",
            TradingModeResolutionSource.StrategyPromotionGuard => "Strateji guard",
            TradingModeResolutionSource.ContextGuard => "Kapsam guard",
            _ => "Global varsayılan"
        };

        return new UserExchangeEnvironmentSummary(
            resolution.EffectiveMode,
            effectiveEnvironmentLabel,
            effectiveEnvironmentTone,
            resolutionSourceLabel,
            resolution.Reason,
            resolution.HasExplicitLiveApproval);
    }

    private static UserExchangeRiskOverrideSummary BuildRiskOverrideSummary(
        RiskProfile? riskProfile,
        UserExecutionOverride? executionOverride)
    {
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
            ? "Kullanıcı için tanımlı risk profili yok. Credential akışı yine çalışır ancak risk görünürlüğü eksik kalır."
            : $"Profil '{riskProfile.ProfileName}' · Günlük kayıp %{riskProfile.MaxDailyLossPercentage:0.##} · Pozisyon %{riskProfile.MaxPositionSizePercentage:0.##} · Maks kaldıraç {riskProfile.MaxLeverage:0.##}x";

        if (executionOverride is not null)
        {
            summaryText = $"{summaryText} · Override: {BuildOverrideSummary(executionOverride)}";
        }

        return new UserExchangeRiskOverrideSummary(
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

    private static UserExchangeAccountSummary MapAccountSummary(
        ExchangeAccount account,
        ApiCredentialValidation? latestValidation,
        ExchangeAccountSyncState? syncState,
        UserExchangeEnvironmentSummary environmentSummary,
        DateTime utcNow)
    {
        var lastValidatedAtUtc = latestValidation?.ValidatedAtUtc ?? account.LastValidatedAt;
        var environmentLabel = latestValidation?.EnvironmentScope ?? environmentSummary.EffectiveEnvironmentLabel;
        var environmentTone = latestValidation?.IsEnvironmentMatch == false
            ? "critical"
            : string.Equals(environmentLabel, "Live", StringComparison.OrdinalIgnoreCase)
                ? "warning"
                : "info";

        return new UserExchangeAccountSummary(
            account.Id,
            account.ExchangeName,
            string.IsNullOrWhiteSpace(account.DisplayName) ? account.ExchangeName : account.DisplayName,
            BuildCredentialStatusLabel(account.CredentialStatus),
            BuildCredentialStatusTone(account.CredentialStatus),
            BuildTradeModeLabel(latestValidation),
            BuildSyncStatusLabel(syncState),
            BuildSyncStatusTone(syncState),
            SensitivePayloadMasker.MaskFingerprint(account.CredentialFingerprint),
            latestValidation?.PermissionSummary ?? BuildFallbackPermissionSummary(account),
            environmentLabel,
            environmentTone,
            lastValidatedAtUtc,
            BuildRelativeTimeLabel(utcNow, lastValidatedAtUtc),
            latestValidation?.FailureReason ?? BuildFallbackFailureReason(account.CredentialStatus));
    }

    private static UserExchangeValidationHistoryEntry MapValidationHistoryEntry(
        ApiCredentialValidation validation,
        ExchangeAccount? account,
        DateTime utcNow)
    {
        return new UserExchangeValidationHistoryEntry(
            validation.ExchangeAccountId,
            account is null
                ? BinanceExchangeName
                : string.IsNullOrWhiteSpace(account.DisplayName) ? account.ExchangeName : account.DisplayName,
            validation.ValidatedAtUtc,
            BuildRelativeTimeLabel(utcNow, validation.ValidatedAtUtc),
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
            SensitivePayloadMasker.MaskFingerprint(account?.CredentialFingerprint));
    }

    private static string ResolveFailureReason(
        BinanceCredentialProbeSnapshot probe,
        ExchangeTradeModeSelection requestedTradeMode,
        ExecutionEnvironment requestedEnvironment,
        ExecutionEnvironment effectiveEnvironment,
        bool requestedEnvironmentAllowed,
        bool providerEnvironmentMatch,
        bool withdrawVerified,
        bool canWithdraw,
        bool canTrade)
    {
        if (!requestedEnvironmentAllowed)
        {
            return $"Etkin işlem modu {effectiveEnvironment} olduğu için {requestedEnvironment} ortamına bağlantı kabul edilmedi.";
        }

        if (!providerEnvironmentMatch)
        {
            return "Seçilen ortam ile Binance uç noktası eşleşmiyor.";
        }

        if (probe.HasTimestampSkew)
        {
            return "Binance zaman damgası doğrulamayı reddetti.";
        }

        if (probe.HasIpRestrictionIssue)
        {
            return "Binance IP kısıtı nedeniyle doğrulamayı reddetti.";
        }

        if (!withdrawVerified)
        {
            return "Çekim izni güvenli şekilde doğrulanamadı.";
        }

        if (canWithdraw)
        {
            return "Withdraw izni açık olduğu için anahtar reddedildi.";
        }

        if (!probe.IsKeyValid)
        {
            return probe.SafeFailureReason ?? "API key veya secret doğrulanamadı.";
        }

        if (!canTrade)
        {
            return requestedTradeMode switch
            {
                ExchangeTradeModeSelection.Spot => "Spot trade izni doğrulanamadı.",
                ExchangeTradeModeSelection.Futures => "Futures erişimi doğrulanamadı.",
                ExchangeTradeModeSelection.Both => "Spot ve futures erişimi birlikte doğrulanamadı.",
                _ => "Trade izni doğrulanamadı."
            };
        }

        return null;
    }

    private static string BuildEffectivePermissionSummary(string permissionSummary, bool demoWithdrawUnknownAccepted)
    {
        if (!demoWithdrawUnknownAccepted)
        {
            return permissionSummary;
        }

        return permissionSummary.Replace("Withdraw=?", "Withdraw=DemoUnknown", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveProviderEnvironmentScope(
        ExchangeTradeModeSelection requestedTradeMode,
        string spotEnvironmentScope,
        string futuresEnvironmentScope)
    {
        return requestedTradeMode switch
        {
            ExchangeTradeModeSelection.Spot => spotEnvironmentScope,
            ExchangeTradeModeSelection.Futures => futuresEnvironmentScope,
            ExchangeTradeModeSelection.Both when string.Equals(spotEnvironmentScope, futuresEnvironmentScope, StringComparison.OrdinalIgnoreCase) => spotEnvironmentScope,
            ExchangeTradeModeSelection.Both => "Mixed",
            _ => "Unknown"
        };
    }

    private static bool IsProviderEnvironmentMatch(
        ExchangeTradeModeSelection requestedTradeMode,
        ExecutionEnvironment requestedEnvironment,
        string spotEnvironmentScope,
        string futuresEnvironmentScope)
    {
        var requestedEnvironmentLabel = requestedEnvironment == ExecutionEnvironment.Live ? "Live" : "Demo";
        return requestedTradeMode switch
        {
            ExchangeTradeModeSelection.Spot =>
                string.Equals(spotEnvironmentScope, requestedEnvironmentLabel, StringComparison.OrdinalIgnoreCase),
            ExchangeTradeModeSelection.Futures =>
                string.Equals(futuresEnvironmentScope, requestedEnvironmentLabel, StringComparison.OrdinalIgnoreCase),
            ExchangeTradeModeSelection.Both =>
                string.Equals(spotEnvironmentScope, requestedEnvironmentLabel, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(futuresEnvironmentScope, requestedEnvironmentLabel, StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    private static string ResolveDisplayName(ApplicationUser user)
    {
        return string.IsNullOrWhiteSpace(user.FullName)
            ? user.UserName ?? user.Email ?? user.Id
            : user.FullName;
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

    private static string BuildCredentialStatusTone(ExchangeCredentialStatus status)
    {
        return status switch
        {
            ExchangeCredentialStatus.Active => "healthy",
            ExchangeCredentialStatus.PendingValidation => "warning",
            ExchangeCredentialStatus.RevalidationRequired => "warning",
            ExchangeCredentialStatus.RotationRequired => "critical",
            ExchangeCredentialStatus.Invalid => "critical",
            _ => "neutral"
        };
    }

    private static string BuildTradeModeLabel(ApiCredentialValidation? validation)
    {
        if (validation is null)
        {
            return "Henüz doğrulama yok";
        }

        if (validation.SupportsSpot && validation.SupportsFutures)
        {
            return "Spot + Futures";
        }

        if (validation.SupportsFutures)
        {
            return "Futures";
        }

        if (validation.SupportsSpot)
        {
            return "Spot";
        }

        return "Uyum doğrulanamadı";
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

    private static string BuildFallbackPermissionSummary(ExchangeAccount account)
    {
        return account.IsReadOnly
            ? "Trade=N; Withdraw=?; Spot=?; Futures=?; Env=Unknown"
            : "Trade=Y; Withdraw=?; Spot=?; Futures=?; Env=Unknown";
    }

    private static string? BuildFallbackFailureReason(ExchangeCredentialStatus status)
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

    private static string BuildRelativeTimeLabel(DateTime utcNow, DateTime? timestamp)
    {
        if (!timestamp.HasValue)
        {
            return "Henüz yok";
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

        if (age < TimeSpan.FromDays(7))
        {
            return $"{Math.Max(1, (int)age.TotalDays)} gün önce";
        }

        return timestamp.Value.ToString("yyyy-MM-dd HH:mm");
    }

    private static string NormalizeRequired(string? value, string parameterName)
    {
        var normalizedValue = value?.Trim();

        if (string.IsNullOrWhiteSpace(normalizedValue))
        {
            throw new ArgumentException("The value is required.", parameterName);
        }

        return normalizedValue;
    }

    private const string BinanceExchangeName = "Binance";
}
