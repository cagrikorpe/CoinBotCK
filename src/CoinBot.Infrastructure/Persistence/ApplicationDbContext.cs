using CoinBot.Application.Abstractions.DataScope;
using CoinBot.Domain.Entities;
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

    public DbSet<ExchangeAccount> ExchangeAccounts => Set<ExchangeAccount>();

    public DbSet<GlobalExecutionSwitch> GlobalExecutionSwitches => Set<GlobalExecutionSwitch>();

    public DbSet<HistoricalMarketCandle> HistoricalMarketCandles => Set<HistoricalMarketCandle>();

    public DbSet<MfaEmailOtpChallenge> MfaEmailOtpChallenges => Set<MfaEmailOtpChallenge>();

    public DbSet<MfaRecoveryCode> MfaRecoveryCodes => Set<MfaRecoveryCode>();

    public DbSet<RiskProfile> RiskProfiles => Set<RiskProfile>();

    public DbSet<TradingBot> TradingBots => Set<TradingBot>();

    public DbSet<TradingStrategy> TradingStrategies => Set<TradingStrategy>();

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
        ConfigureExchangeAccounts(builder.Entity<ExchangeAccount>());
        ConfigureGlobalExecutionSwitches(builder.Entity<GlobalExecutionSwitch>());
        ConfigureHistoricalMarketCandles(builder.Entity<HistoricalMarketCandle>());
        ConfigureMfaEmailOtpChallenges(builder.Entity<MfaEmailOtpChallenge>());
        ConfigureMfaRecoveryCodes(builder.Entity<MfaRecoveryCode>());
        ConfigureRiskProfiles(builder.Entity<RiskProfile>());
        ConfigureTradingBots(builder.Entity<TradingBot>());
        ConfigureTradingStrategies(builder.Entity<TradingStrategy>());
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
    private void ConfigureExchangeAccounts(EntityTypeBuilder<ExchangeAccount> builder)
    {
        ConfigureUserOwnedEntity(builder, "ExchangeAccounts");

        builder.Property(entity => entity.ExchangeName)
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(entity => entity.DisplayName)
            .HasMaxLength(128)
            .IsRequired();
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
