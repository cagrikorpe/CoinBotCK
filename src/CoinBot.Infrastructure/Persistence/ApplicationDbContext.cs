using CoinBot.Application.Abstractions.DataScope;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CoinBot.Infrastructure.Persistence;

public sealed class ApplicationDbContext : IdentityDbContext<ApplicationUser, IdentityRole, string>
{
    private readonly string? currentUserId;
    private readonly bool hasIsolationBypass;

    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options, IDataScopeContext dataScopeContext)
        : base(options)
    {
        currentUserId = string.IsNullOrWhiteSpace(dataScopeContext.UserId)
            ? null
            : dataScopeContext.UserId.Trim();
        hasIsolationBypass = dataScopeContext.HasIsolationBypass;
    }

    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    public DbSet<BackgroundJobLock> BackgroundJobLocks => Set<BackgroundJobLock>();

    public DbSet<BackgroundJobState> BackgroundJobStates => Set<BackgroundJobState>();

    public DbSet<DegradedModeState> DegradedModeStates => Set<DegradedModeState>();

    public DbSet<DemoLedgerEntry> DemoLedgerEntries => Set<DemoLedgerEntry>();

    public DbSet<DemoLedgerTransaction> DemoLedgerTransactions => Set<DemoLedgerTransaction>();

    public DbSet<DemoPosition> DemoPositions => Set<DemoPosition>();

    public DbSet<DemoWallet> DemoWallets => Set<DemoWallet>();

    public DbSet<ExchangeAccount> ExchangeAccounts => Set<ExchangeAccount>();

    public DbSet<ExchangeAccountSyncState> ExchangeAccountSyncStates => Set<ExchangeAccountSyncState>();

    public DbSet<ExchangeBalance> ExchangeBalances => Set<ExchangeBalance>();

    public DbSet<ExchangePosition> ExchangePositions => Set<ExchangePosition>();

    public DbSet<GlobalExecutionSwitch> GlobalExecutionSwitches => Set<GlobalExecutionSwitch>();

    public DbSet<HistoricalMarketCandle> HistoricalMarketCandles => Set<HistoricalMarketCandle>();

    public DbSet<MfaEmailOtpChallenge> MfaEmailOtpChallenges => Set<MfaEmailOtpChallenge>();

    public DbSet<MfaRecoveryCode> MfaRecoveryCodes => Set<MfaRecoveryCode>();

    public DbSet<RiskProfile> RiskProfiles => Set<RiskProfile>();

    public DbSet<TradingBot> TradingBots => Set<TradingBot>();

    public DbSet<TradingStrategy> TradingStrategies => Set<TradingStrategy>();

    public DbSet<TradingStrategySignal> TradingStrategySignals => Set<TradingStrategySignal>();

    public DbSet<TradingStrategySignalVeto> TradingStrategySignalVetoes => Set<TradingStrategySignalVeto>();

    public DbSet<TradingStrategyVersion> TradingStrategyVersions => Set<TradingStrategyVersion>();

    public override int SaveChanges()
    {
        ApplyOwnershipRules();
        ApplyAuditRules();
        return base.SaveChanges();
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        ApplyOwnershipRules();
        ApplyAuditRules();
        return base.SaveChangesAsync(cancellationToken);
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        ConfigureApplicationUsers(builder.Entity<ApplicationUser>());
        ConfigureAuditLogs(builder.Entity<AuditLog>());
        ConfigureBackgroundJobLocks(builder.Entity<BackgroundJobLock>());
        ConfigureBackgroundJobStates(builder.Entity<BackgroundJobState>());
        ConfigureDegradedModeStates(builder.Entity<DegradedModeState>());
        ConfigureDemoLedgerEntries(builder.Entity<DemoLedgerEntry>());
        ConfigureDemoLedgerTransactions(builder.Entity<DemoLedgerTransaction>());
        ConfigureDemoPositions(builder.Entity<DemoPosition>());
        ConfigureDemoWallets(builder.Entity<DemoWallet>());
        ConfigureExchangeAccounts(builder.Entity<ExchangeAccount>());
        ConfigureExchangeAccountSyncStates(builder.Entity<ExchangeAccountSyncState>());
        ConfigureExchangeBalances(builder.Entity<ExchangeBalance>());
        ConfigureExchangePositions(builder.Entity<ExchangePosition>());
        ConfigureGlobalExecutionSwitches(builder.Entity<GlobalExecutionSwitch>());
        ConfigureHistoricalMarketCandles(builder.Entity<HistoricalMarketCandle>());
        ConfigureMfaEmailOtpChallenges(builder.Entity<MfaEmailOtpChallenge>());
        ConfigureMfaRecoveryCodes(builder.Entity<MfaRecoveryCode>());
        ConfigureRiskProfiles(builder.Entity<RiskProfile>());
        ConfigureTradingBots(builder.Entity<TradingBot>());
        ConfigureTradingStrategies(builder.Entity<TradingStrategy>());
        ConfigureTradingStrategySignals(builder.Entity<TradingStrategySignal>());
        ConfigureTradingStrategySignalVetoes(builder.Entity<TradingStrategySignalVeto>());
        ConfigureTradingStrategyVersions(builder.Entity<TradingStrategyVersion>());
    }

    private string? CurrentUserId => currentUserId;

    private bool HasIsolationBypass => hasIsolationBypass;

    private static void ConfigureApplicationUsers(EntityTypeBuilder<ApplicationUser> builder)
    {
        builder.Property(user => user.PreferredMfaProvider)
            .HasMaxLength(32);

        builder.Property(user => user.TotpSecretCiphertext)
            .HasMaxLength(2048);

        builder.Property(user => user.TradingModeOverride)
            .HasConversion<string>()
            .HasMaxLength(32);

        builder.Property(user => user.TradingModeApprovalReference)
            .HasMaxLength(128);
    }

    private static void ConfigureAuditLogs(EntityTypeBuilder<AuditLog> builder)
    {
        builder.ToTable("AuditLogs");

        builder.Property(entity => entity.Actor)
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(entity => entity.Action)
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(entity => entity.Target)
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(entity => entity.Context)
            .HasMaxLength(2048);

        builder.Property(entity => entity.CorrelationId)
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(entity => entity.Outcome)
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(entity => entity.Environment)
            .HasMaxLength(32)
            .IsRequired();

        builder.HasIndex(entity => entity.CorrelationId);
        builder.HasIndex(entity => new { entity.Action, entity.CreatedDate });
        builder.HasIndex(entity => new { entity.Target, entity.CreatedDate });
    }

    private static void ConfigureBackgroundJobLocks(EntityTypeBuilder<BackgroundJobLock> builder)
    {
        builder.ToTable("BackgroundJobLocks");

        builder.Property(entity => entity.JobKey)
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(entity => entity.JobType)
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(entity => entity.WorkerInstanceId)
            .HasMaxLength(128)
            .IsRequired();

        builder.HasIndex(entity => entity.JobKey)
            .IsUnique();

        builder.HasIndex(entity => new { entity.JobType, entity.LeaseExpiresAtUtc });
    }

    private static void ConfigureBackgroundJobStates(EntityTypeBuilder<BackgroundJobState> builder)
    {
        builder.ToTable("BackgroundJobStates");

        builder.Property(entity => entity.JobKey)
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(entity => entity.JobType)
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(entity => entity.Status)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(entity => entity.IdempotencyKey)
            .HasMaxLength(128);

        builder.Property(entity => entity.LastErrorCode)
            .HasMaxLength(64);

        builder.HasIndex(entity => entity.JobKey)
            .IsUnique();

        builder.HasIndex(entity => new { entity.JobType, entity.Status, entity.NextRunAtUtc });

        builder.HasIndex(entity => entity.BotId);
    }

    private static void ConfigureDegradedModeStates(EntityTypeBuilder<DegradedModeState> builder)
    {
        builder.ToTable("DegradedModeStates");

        builder.Property(entity => entity.StateCode)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(entity => entity.ReasonCode)
            .HasConversion<string>()
            .HasMaxLength(64)
            .IsRequired();
    }

    private void ConfigureDemoLedgerEntries(EntityTypeBuilder<DemoLedgerEntry> builder)
    {
        ConfigureUserOwnedEntity(builder, "DemoLedgerEntries");

        builder.Property(entity => entity.Asset)
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(entity => entity.AvailableDelta)
            .HasPrecision(38, 18);

        builder.Property(entity => entity.ReservedDelta)
            .HasPrecision(38, 18);

        builder.Property(entity => entity.AvailableBalanceAfter)
            .HasPrecision(38, 18);

        builder.Property(entity => entity.ReservedBalanceAfter)
            .HasPrecision(38, 18);

        builder.HasIndex(entity => entity.DemoLedgerTransactionId);

        builder.HasOne<DemoLedgerTransaction>()
            .WithMany()
            .HasForeignKey(entity => entity.DemoLedgerTransactionId)
            .OnDelete(DeleteBehavior.Cascade);
    }

    private void ConfigureDemoLedgerTransactions(EntityTypeBuilder<DemoLedgerTransaction> builder)
    {
        ConfigureUserOwnedEntity(builder, "DemoLedgerTransactions");

        builder.Property(entity => entity.OperationId)
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(entity => entity.TransactionType)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(entity => entity.PositionScopeKey)
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(entity => entity.OrderId)
            .HasMaxLength(128);

        builder.Property(entity => entity.FillId)
            .HasMaxLength(128);

        builder.Property(entity => entity.Symbol)
            .HasMaxLength(32);

        builder.Property(entity => entity.BaseAsset)
            .HasMaxLength(32);

        builder.Property(entity => entity.QuoteAsset)
            .HasMaxLength(32);

        builder.Property(entity => entity.PositionKind)
            .HasConversion<string>()
            .HasMaxLength(16);

        builder.Property(entity => entity.MarginMode)
            .HasConversion<string>()
            .HasMaxLength(16);

        builder.Property(entity => entity.Side)
            .HasConversion<string>()
            .HasMaxLength(16);

        builder.Property(entity => entity.FeeAsset)
            .HasMaxLength(32);

        builder.Property(entity => entity.Price)
            .HasPrecision(38, 18);

        builder.Property(entity => entity.Quantity)
            .HasPrecision(38, 18);

        builder.Property(entity => entity.FeeAmount)
            .HasPrecision(38, 18);

        builder.Property(entity => entity.FeeAmountInQuote)
            .HasPrecision(38, 18);

        builder.Property(entity => entity.Leverage)
            .HasPrecision(18, 8);

        builder.Property(entity => entity.FundingRate)
            .HasPrecision(18, 8);

        builder.Property(entity => entity.FundingDeltaInQuote)
            .HasPrecision(38, 18);

        builder.Property(entity => entity.RealizedPnlDelta)
            .HasPrecision(38, 18);

        builder.Property(entity => entity.PositionQuantityAfter)
            .HasPrecision(38, 18);

        builder.Property(entity => entity.PositionCostBasisAfter)
            .HasPrecision(38, 18);

        builder.Property(entity => entity.PositionAverageEntryPriceAfter)
            .HasPrecision(38, 18);

        builder.Property(entity => entity.CumulativeRealizedPnlAfter)
            .HasPrecision(38, 18);

        builder.Property(entity => entity.UnrealizedPnlAfter)
            .HasPrecision(38, 18);

        builder.Property(entity => entity.CumulativeFeesInQuoteAfter)
            .HasPrecision(38, 18);

        builder.Property(entity => entity.NetFundingInQuoteAfter)
            .HasPrecision(38, 18);

        builder.Property(entity => entity.LastPriceAfter)
            .HasPrecision(38, 18);

        builder.Property(entity => entity.MarkPriceAfter)
            .HasPrecision(38, 18);

        builder.Property(entity => entity.MaintenanceMarginRateAfter)
            .HasPrecision(18, 8);

        builder.Property(entity => entity.MaintenanceMarginAfter)
            .HasPrecision(38, 18);

        builder.Property(entity => entity.MarginBalanceAfter)
            .HasPrecision(38, 18);

        builder.Property(entity => entity.LiquidationPriceAfter)
            .HasPrecision(38, 18);

        builder.HasIndex(entity => new { entity.OwnerUserId, entity.OperationId })
            .IsUnique();

        builder.HasIndex(entity => new { entity.OwnerUserId, entity.OccurredAtUtc });
    }

    private void ConfigureDemoPositions(EntityTypeBuilder<DemoPosition> builder)
    {
        ConfigureUserOwnedEntity(builder, "DemoPositions");

        builder.Property(entity => entity.PositionScopeKey)
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(entity => entity.Symbol)
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(entity => entity.BaseAsset)
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(entity => entity.QuoteAsset)
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(entity => entity.PositionKind)
            .HasConversion<string>()
            .HasMaxLength(16)
            .HasDefaultValue(DemoPositionKind.Spot)
            .IsRequired();

        builder.Property(entity => entity.MarginMode)
            .HasConversion<string>()
            .HasMaxLength(16);

        builder.Property(entity => entity.Leverage)
            .HasPrecision(18, 8);

        builder.Property(entity => entity.Quantity)
            .HasPrecision(38, 18);

        builder.Property(entity => entity.CostBasis)
            .HasPrecision(38, 18);

        builder.Property(entity => entity.AverageEntryPrice)
            .HasPrecision(38, 18);

        builder.Property(entity => entity.RealizedPnl)
            .HasPrecision(38, 18);

        builder.Property(entity => entity.UnrealizedPnl)
            .HasPrecision(38, 18);

        builder.Property(entity => entity.TotalFeesInQuote)
            .HasPrecision(38, 18);

        builder.Property(entity => entity.NetFundingInQuote)
            .HasPrecision(38, 18);

        builder.Property(entity => entity.IsolatedMargin)
            .HasPrecision(38, 18);

        builder.Property(entity => entity.MaintenanceMarginRate)
            .HasPrecision(18, 8);

        builder.Property(entity => entity.MaintenanceMargin)
            .HasPrecision(38, 18);

        builder.Property(entity => entity.MarginBalance)
            .HasPrecision(38, 18);

        builder.Property(entity => entity.LiquidationPrice)
            .HasPrecision(38, 18);

        builder.Property(entity => entity.LastMarkPrice)
            .HasPrecision(38, 18);

        builder.Property(entity => entity.LastPrice)
            .HasPrecision(38, 18);

        builder.Property(entity => entity.LastFillPrice)
            .HasPrecision(38, 18);

        builder.Property(entity => entity.LastFundingRate)
            .HasPrecision(18, 8);

        builder.HasIndex(entity => new { entity.OwnerUserId, entity.PositionScopeKey, entity.Symbol })
            .IsUnique();

        builder.HasIndex(entity => entity.BotId);
    }

    private void ConfigureDemoWallets(EntityTypeBuilder<DemoWallet> builder)
    {
        ConfigureUserOwnedEntity(builder, "DemoWallets");

        builder.Property(entity => entity.Asset)
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(entity => entity.AvailableBalance)
            .HasPrecision(38, 18);

        builder.Property(entity => entity.ReservedBalance)
            .HasPrecision(38, 18);

        builder.HasIndex(entity => new { entity.OwnerUserId, entity.Asset })
            .IsUnique();
    }

    private void ConfigureExchangeAccounts(EntityTypeBuilder<ExchangeAccount> builder)
    {
        ConfigureUserOwnedEntity(builder, "ExchangeAccounts");

        builder.Property(entity => entity.ExchangeName)
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(entity => entity.DisplayName)
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(entity => entity.ApiKeyCiphertext)
            .HasMaxLength(4096);

        builder.Property(entity => entity.ApiSecretCiphertext)
            .HasMaxLength(4096);

        builder.Property(entity => entity.CredentialFingerprint)
            .HasMaxLength(128);

        builder.Property(entity => entity.CredentialKeyVersion)
            .HasMaxLength(64);

        builder.Property(entity => entity.CredentialStatus)
            .HasConversion<string>()
            .HasMaxLength(32)
            .HasDefaultValue(ExchangeCredentialStatus.Missing)
            .IsRequired();

        builder.HasIndex(entity => new
        {
            entity.CredentialStatus,
            entity.CredentialRevalidateAfterUtc
        });

        builder.HasIndex(entity => new
        {
            entity.CredentialStatus,
            entity.CredentialRotateAfterUtc
        });
    }

    private void ConfigureExchangeAccountSyncStates(EntityTypeBuilder<ExchangeAccountSyncState> builder)
    {
        ConfigureUserOwnedEntity(builder, "ExchangeAccountSyncStates");

        builder.Property(entity => entity.PrivateStreamConnectionState)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(entity => entity.DriftStatus)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(entity => entity.DriftSummary)
            .HasMaxLength(512);

        builder.Property(entity => entity.LastErrorCode)
            .HasMaxLength(64);

        builder.HasIndex(entity => entity.ExchangeAccountId)
            .IsUnique();

        builder.HasOne<ExchangeAccount>()
            .WithMany()
            .HasForeignKey(entity => entity.ExchangeAccountId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    private void ConfigureExchangeBalances(EntityTypeBuilder<ExchangeBalance> builder)
    {
        ConfigureUserOwnedEntity(builder, "ExchangeBalances");

        builder.Property(entity => entity.Asset)
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(entity => entity.WalletBalance)
            .HasPrecision(38, 18);

        builder.Property(entity => entity.CrossWalletBalance)
            .HasPrecision(38, 18);

        builder.Property(entity => entity.AvailableBalance)
            .HasPrecision(38, 18);

        builder.Property(entity => entity.MaxWithdrawAmount)
            .HasPrecision(38, 18);

        builder.HasIndex(entity => new { entity.ExchangeAccountId, entity.Asset })
            .IsUnique();

        builder.HasOne<ExchangeAccount>()
            .WithMany()
            .HasForeignKey(entity => entity.ExchangeAccountId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    private void ConfigureExchangePositions(EntityTypeBuilder<ExchangePosition> builder)
    {
        ConfigureUserOwnedEntity(builder, "ExchangePositions");

        builder.Property(entity => entity.Symbol)
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(entity => entity.PositionSide)
            .HasMaxLength(16)
            .IsRequired();

        builder.Property(entity => entity.MarginType)
            .HasMaxLength(16)
            .IsRequired();

        builder.Property(entity => entity.Quantity)
            .HasPrecision(38, 18);

        builder.Property(entity => entity.EntryPrice)
            .HasPrecision(38, 18);

        builder.Property(entity => entity.BreakEvenPrice)
            .HasPrecision(38, 18);

        builder.Property(entity => entity.UnrealizedProfit)
            .HasPrecision(38, 18);

        builder.Property(entity => entity.IsolatedWallet)
            .HasPrecision(38, 18);

        builder.HasIndex(entity => new { entity.ExchangeAccountId, entity.Symbol, entity.PositionSide })
            .IsUnique();

        builder.HasOne<ExchangeAccount>()
            .WithMany()
            .HasForeignKey(entity => entity.ExchangeAccountId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureGlobalExecutionSwitches(EntityTypeBuilder<GlobalExecutionSwitch> builder)
    {
        builder.ToTable("GlobalExecutionSwitches");

        builder.Property(entity => entity.TradeMasterState)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(entity => entity.LiveModeApprovalReference)
            .HasMaxLength(128);
    }

    private static void ConfigureHistoricalMarketCandles(EntityTypeBuilder<HistoricalMarketCandle> builder)
    {
        builder.ToTable("HistoricalMarketCandles");

        builder.Property(entity => entity.Symbol)
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(entity => entity.Interval)
            .HasMaxLength(16)
            .IsRequired();

        builder.Property(entity => entity.OpenPrice)
            .HasPrecision(38, 18);

        builder.Property(entity => entity.HighPrice)
            .HasPrecision(38, 18);

        builder.Property(entity => entity.LowPrice)
            .HasPrecision(38, 18);

        builder.Property(entity => entity.ClosePrice)
            .HasPrecision(38, 18);

        builder.Property(entity => entity.Volume)
            .HasPrecision(38, 18);

        builder.Property(entity => entity.Source)
            .HasMaxLength(64)
            .IsRequired();

        builder.HasIndex(entity => new { entity.Symbol, entity.Interval, entity.OpenTimeUtc })
            .IsUnique();

        builder.HasIndex(entity => new { entity.Symbol, entity.Interval, entity.CloseTimeUtc });
    }

    private static void ConfigureMfaEmailOtpChallenges(EntityTypeBuilder<MfaEmailOtpChallenge> builder)
    {
        builder.ToTable("MfaEmailOtpChallenges");

        builder.Property(entity => entity.UserId)
            .HasMaxLength(450)
            .IsRequired();

        builder.Property(entity => entity.Purpose)
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(entity => entity.TokenCiphertext)
            .HasMaxLength(1024)
            .IsRequired();

        builder.HasIndex(entity => new
        {
            entity.UserId,
            entity.Purpose,
            entity.ConsumedAtUtc,
            entity.ExpiresAtUtc
        });

        builder.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(entity => entity.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }

    private static void ConfigureMfaRecoveryCodes(EntityTypeBuilder<MfaRecoveryCode> builder)
    {
        builder.ToTable("MfaRecoveryCodes");

        builder.Property(entity => entity.UserId)
            .HasMaxLength(450)
            .IsRequired();

        builder.Property(entity => entity.CodeHash)
            .HasMaxLength(256)
            .IsRequired();

        builder.HasIndex(entity => new
        {
            entity.UserId,
            entity.ConsumedAtUtc
        });

        builder.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(entity => entity.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }

    private void ConfigureRiskProfiles(EntityTypeBuilder<RiskProfile> builder)
    {
        ConfigureUserOwnedEntity(builder, "RiskProfiles");

        builder.Property(entity => entity.ProfileName)
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(entity => entity.MaxDailyLossPercentage)
            .HasPrecision(18, 4);

        builder.Property(entity => entity.MaxPositionSizePercentage)
            .HasPrecision(18, 4);

        builder.Property(entity => entity.MaxLeverage)
            .HasPrecision(18, 4)
            .HasDefaultValue(1m);
    }

    private void ConfigureTradingBots(EntityTypeBuilder<TradingBot> builder)
    {
        ConfigureUserOwnedEntity(builder, "TradingBots");

        builder.Property(entity => entity.Name)
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(entity => entity.StrategyKey)
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(entity => entity.TradingModeOverride)
            .HasConversion<string>()
            .HasMaxLength(32);

        builder.Property(entity => entity.TradingModeApprovalReference)
            .HasMaxLength(128);
    }

    private void ConfigureTradingStrategies(EntityTypeBuilder<TradingStrategy> builder)
    {
        ConfigureUserOwnedEntity(builder, "TradingStrategies");

        builder.Property(entity => entity.StrategyKey)
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(entity => entity.DisplayName)
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(entity => entity.PromotionState)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(entity => entity.PublishedMode)
            .HasConversion<string>()
            .HasMaxLength(32);

        builder.Property(entity => entity.LivePromotionApprovalReference)
            .HasMaxLength(128);

        builder.HasIndex(entity => new { entity.OwnerUserId, entity.StrategyKey })
            .IsUnique();
    }

    private void ConfigureTradingStrategySignals(EntityTypeBuilder<TradingStrategySignal> builder)
    {
        ConfigureUserOwnedEntity(builder, "TradingStrategySignals");

        builder.Property(entity => entity.StrategyVersionNumber)
            .IsRequired();

        builder.Property(entity => entity.StrategySchemaVersion)
            .IsRequired();

        builder.Property(entity => entity.SignalType)
            .HasConversion<string>()
            .HasMaxLength(16)
            .IsRequired();

        builder.Property(entity => entity.ExecutionEnvironment)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(entity => entity.Symbol)
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(entity => entity.Timeframe)
            .HasMaxLength(16)
            .IsRequired();

        builder.Property(entity => entity.GeneratedAtUtc)
            .IsRequired();

        builder.Property(entity => entity.ExplainabilitySchemaVersion)
            .HasDefaultValue(1)
            .IsRequired();

        builder.Property(entity => entity.IndicatorSnapshotJson)
            .IsRequired();

        builder.Property(entity => entity.RuleResultSnapshotJson)
            .IsRequired();

        builder.Property(entity => entity.RiskEvaluationJson);

        builder.HasIndex(entity => entity.TradingStrategyId);

        builder.HasIndex(entity => entity.TradingStrategyVersionId);

        builder.HasIndex(entity => new { entity.TradingStrategyId, entity.GeneratedAtUtc });

        builder.HasIndex(entity => new
            {
                entity.TradingStrategyVersionId,
                entity.SignalType,
                entity.Symbol,
                entity.Timeframe,
                entity.IndicatorCloseTimeUtc
            })
            .IsUnique();

        builder.HasOne<TradingStrategy>()
            .WithMany()
            .HasForeignKey(entity => entity.TradingStrategyId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<TradingStrategyVersion>()
            .WithMany()
            .HasForeignKey(entity => entity.TradingStrategyVersionId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    private void ConfigureTradingStrategySignalVetoes(EntityTypeBuilder<TradingStrategySignalVeto> builder)
    {
        ConfigureUserOwnedEntity(builder, "TradingStrategySignalVetoes");

        builder.Property(entity => entity.StrategyVersionNumber)
            .IsRequired();

        builder.Property(entity => entity.StrategySchemaVersion)
            .IsRequired();

        builder.Property(entity => entity.SignalType)
            .HasConversion<string>()
            .HasMaxLength(16)
            .IsRequired();

        builder.Property(entity => entity.ExecutionEnvironment)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(entity => entity.Symbol)
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(entity => entity.Timeframe)
            .HasMaxLength(16)
            .IsRequired();

        builder.Property(entity => entity.ReasonCode)
            .HasConversion<string>()
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(entity => entity.RiskEvaluationJson)
            .IsRequired();

        builder.HasIndex(entity => entity.TradingStrategyId);

        builder.HasIndex(entity => entity.TradingStrategyVersionId);

        builder.HasIndex(entity => new { entity.TradingStrategyId, entity.EvaluatedAtUtc });

        builder.HasIndex(entity => new
            {
                entity.TradingStrategyVersionId,
                entity.SignalType,
                entity.Symbol,
                entity.Timeframe,
                entity.IndicatorCloseTimeUtc,
                entity.ReasonCode
            })
            .IsUnique();

        builder.HasOne<TradingStrategy>()
            .WithMany()
            .HasForeignKey(entity => entity.TradingStrategyId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<TradingStrategyVersion>()
            .WithMany()
            .HasForeignKey(entity => entity.TradingStrategyVersionId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    private void ConfigureTradingStrategyVersions(EntityTypeBuilder<TradingStrategyVersion> builder)
    {
        ConfigureUserOwnedEntity(builder, "TradingStrategyVersions");

        builder.Property(entity => entity.SchemaVersion)
            .IsRequired();

        builder.Property(entity => entity.VersionNumber)
            .IsRequired();

        builder.Property(entity => entity.Status)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(entity => entity.DefinitionJson)
            .IsRequired();

        builder.HasIndex(entity => new { entity.TradingStrategyId, entity.VersionNumber })
            .IsUnique();

        builder.HasIndex(entity => new { entity.TradingStrategyId, entity.Status });

        builder.HasIndex(entity => entity.TradingStrategyId)
            .HasDatabaseName("IX_TradingStrategyVersions_TradingStrategyId_Draft")
            .HasFilter("[Status] = N'Draft'")
            .IsUnique();

        builder.HasIndex(entity => entity.TradingStrategyId)
            .HasDatabaseName("IX_TradingStrategyVersions_TradingStrategyId_Published")
            .HasFilter("[Status] = N'Published'")
            .IsUnique();

        builder.HasOne<TradingStrategy>()
            .WithMany()
            .HasForeignKey(entity => entity.TradingStrategyId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    private void ConfigureUserOwnedEntity<TEntity>(EntityTypeBuilder<TEntity> builder, string tableName)
        where TEntity : UserOwnedEntity
    {
        builder.ToTable(tableName);

        builder.Property(entity => entity.OwnerUserId)
            .HasMaxLength(450)
            .IsRequired();

        builder.HasIndex(entity => entity.OwnerUserId);

        builder.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(entity => entity.OwnerUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasQueryFilter(entity =>
            HasIsolationBypass || (CurrentUserId != null && entity.OwnerUserId == CurrentUserId));
    }

    private void ApplyOwnershipRules()
    {
        foreach (var entry in ChangeTracker.Entries<UserOwnedEntity>())
        {
            var ownerUserId = NormalizeOwnerUserId(entry.Entity.OwnerUserId);

            switch (entry.State)
            {
                case EntityState.Added:
                    entry.Entity.OwnerUserId = ResolveOwnerUserIdForCreate(entry.Metadata.ClrType.Name, ownerUserId);
                    break;
                case EntityState.Modified:
                    var originalOwnerUserId = NormalizeOwnerUserId(entry.OriginalValues.GetValue<string>(nameof(UserOwnedEntity.OwnerUserId)));

                    if (!string.Equals(originalOwnerUserId, ownerUserId, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException($"Ownership cannot be reassigned for user-scoped entity '{entry.Metadata.ClrType.Name}'.");
                    }

                    EnsureOwnerAccess(entry.Metadata.ClrType.Name, ownerUserId);
                    entry.Entity.OwnerUserId = ownerUserId;
                    break;
                case EntityState.Deleted:
                    EnsureOwnerAccess(entry.Metadata.ClrType.Name, ownerUserId);
                    break;
            }
        }
    }

    private string ResolveOwnerUserIdForCreate(string entityName, string ownerUserId)
    {
        if (string.IsNullOrWhiteSpace(ownerUserId))
        {
            if (string.IsNullOrWhiteSpace(CurrentUserId))
            {
                throw new InvalidOperationException($"User-scoped entity '{entityName}' requires an active user scope.");
            }

            return CurrentUserId!;
        }

        EnsureOwnerAccess(entityName, ownerUserId);
        return ownerUserId;
    }

    private void EnsureOwnerAccess(string entityName, string ownerUserId)
    {
        if (HasIsolationBypass)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(CurrentUserId) || !string.Equals(ownerUserId, CurrentUserId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"User scope mismatch detected for entity '{entityName}'.");
        }
    }

    private static string NormalizeOwnerUserId(string? ownerUserId)
    {
        return ownerUserId?.Trim() ?? string.Empty;
    }

    private void ApplyAuditRules()
    {
        var utcNow = DateTime.UtcNow;

        foreach (var entry in ChangeTracker.Entries<BaseEntity>())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    entry.Entity.CreatedDate = utcNow;
                    entry.Entity.UpdatedDate = utcNow;
                    entry.Entity.IsDeleted = false;
                    break;
                case EntityState.Modified:
                    entry.Entity.UpdatedDate = utcNow;
                    break;
                case EntityState.Deleted:
                    entry.State = EntityState.Modified;
                    entry.Entity.IsDeleted = true;
                    entry.Entity.UpdatedDate = utcNow;
                    break;
            }
        }
    }
}
