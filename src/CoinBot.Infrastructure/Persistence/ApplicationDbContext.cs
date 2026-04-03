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

    public DbSet<AdminAuditLog> AdminAuditLogs => Set<AdminAuditLog>();

    public DbSet<AdminCommandRegistryEntry> AdminCommandRegistryEntries => Set<AdminCommandRegistryEntry>();

    public DbSet<ApiCredential> ApiCredentials => Set<ApiCredential>();

    public DbSet<ApiCredentialValidation> ApiCredentialValidations => Set<ApiCredentialValidation>();

    public DbSet<ApprovalAction> ApprovalActions => Set<ApprovalAction>();

    public DbSet<ApprovalQueue> ApprovalQueues => Set<ApprovalQueue>();

    public DbSet<AutonomyReviewQueueEntry> AutonomyReviewQueueEntries => Set<AutonomyReviewQueueEntry>();

    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    public DbSet<BackgroundJobLock> BackgroundJobLocks => Set<BackgroundJobLock>();

    public DbSet<BackgroundJobState> BackgroundJobStates => Set<BackgroundJobState>();

    public DbSet<DegradedModeState> DegradedModeStates => Set<DegradedModeState>();

    public DbSet<DependencyCircuitBreakerState> DependencyCircuitBreakerStates => Set<DependencyCircuitBreakerState>();

    public DbSet<DecisionTrace> DecisionTraces => Set<DecisionTrace>();

    public DbSet<DemoSession> DemoSessions => Set<DemoSession>();

    public DbSet<DemoLedgerEntry> DemoLedgerEntries => Set<DemoLedgerEntry>();

    public DbSet<DemoLedgerTransaction> DemoLedgerTransactions => Set<DemoLedgerTransaction>();

    public DbSet<DemoPosition> DemoPositions => Set<DemoPosition>();

    public DbSet<DemoWallet> DemoWallets => Set<DemoWallet>();

    public DbSet<ExecutionOrder> ExecutionOrders => Set<ExecutionOrder>();

    public DbSet<ExecutionOrderTransition> ExecutionOrderTransitions => Set<ExecutionOrderTransition>();

    public DbSet<ExecutionTrace> ExecutionTraces => Set<ExecutionTrace>();

    public DbSet<ExchangeAccount> ExchangeAccounts => Set<ExchangeAccount>();

    public DbSet<ExchangeAccountSyncState> ExchangeAccountSyncStates => Set<ExchangeAccountSyncState>();

    public DbSet<ExchangeBalance> ExchangeBalances => Set<ExchangeBalance>();

    public DbSet<ExchangePosition> ExchangePositions => Set<ExchangePosition>();

    public DbSet<HealthSnapshot> HealthSnapshots => Set<HealthSnapshot>();

    public DbSet<GlobalExecutionSwitch> GlobalExecutionSwitches => Set<GlobalExecutionSwitch>();

    public DbSet<GlobalSystemState> GlobalSystemStates => Set<GlobalSystemState>();

    public DbSet<Incident> Incidents => Set<Incident>();

    public DbSet<IncidentEvent> IncidentEvents => Set<IncidentEvent>();

    public DbSet<SystemStateHistory> SystemStateHistories => Set<SystemStateHistory>();

    public DbSet<RiskPolicy> RiskPolicies => Set<RiskPolicy>();

    public DbSet<RiskPolicyVersion> RiskPolicyVersions => Set<RiskPolicyVersion>();

    public DbSet<HistoricalMarketCandle> HistoricalMarketCandles => Set<HistoricalMarketCandle>();

    public DbSet<MfaEmailOtpChallenge> MfaEmailOtpChallenges => Set<MfaEmailOtpChallenge>();

    public DbSet<MfaRecoveryCode> MfaRecoveryCodes => Set<MfaRecoveryCode>();

    public DbSet<MarketScannerCycle> MarketScannerCycles => Set<MarketScannerCycle>();

    public DbSet<MarketScannerCandidate> MarketScannerCandidates => Set<MarketScannerCandidate>();

    public DbSet<MarketScannerHandoffAttempt> MarketScannerHandoffAttempts => Set<MarketScannerHandoffAttempt>();

    public DbSet<RiskProfile> RiskProfiles => Set<RiskProfile>();

    public DbSet<TradingBot> TradingBots => Set<TradingBot>();

    public DbSet<TradingStrategy> TradingStrategies => Set<TradingStrategy>();

    public DbSet<TradingStrategySignal> TradingStrategySignals => Set<TradingStrategySignal>();

    public DbSet<TradingStrategySignalVeto> TradingStrategySignalVetoes => Set<TradingStrategySignalVeto>();

    public DbSet<TradingStrategyVersion> TradingStrategyVersions => Set<TradingStrategyVersion>();

    public DbSet<WorkerHeartbeat> WorkerHeartbeats => Set<WorkerHeartbeat>();

    public DbSet<UserExecutionOverride> UserExecutionOverrides => Set<UserExecutionOverride>();

    public override int SaveChanges()
    {
        ApplyAppendOnlyRules();
        ApplyOwnershipRules();
        ApplyAuditRules();
        return base.SaveChanges();
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        ApplyAppendOnlyRules();
        ApplyOwnershipRules();
        ApplyAuditRules();
        return base.SaveChangesAsync(cancellationToken);
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        ConfigureApplicationUsers(builder.Entity<ApplicationUser>());
        ConfigureAdminAuditLogs(builder.Entity<AdminAuditLog>());
        ConfigureAdminCommandRegistryEntries(builder.Entity<AdminCommandRegistryEntry>());
        ConfigureApiCredentials(builder.Entity<ApiCredential>());
        ConfigureApiCredentialValidations(builder.Entity<ApiCredentialValidation>());
        ConfigureApprovalActions(builder.Entity<ApprovalAction>());
        ConfigureApprovalQueues(builder.Entity<ApprovalQueue>());
        ConfigureAutonomyReviewQueueEntries(builder.Entity<AutonomyReviewQueueEntry>());
        ConfigureAuditLogs(builder.Entity<AuditLog>());
        ConfigureBackgroundJobLocks(builder.Entity<BackgroundJobLock>());
        ConfigureBackgroundJobStates(builder.Entity<BackgroundJobState>());
        ConfigureDegradedModeStates(builder.Entity<DegradedModeState>());
        ConfigureDependencyCircuitBreakerStates(builder.Entity<DependencyCircuitBreakerState>());
        ConfigureDecisionTraces(builder.Entity<DecisionTrace>());
        ConfigureDemoSessions(builder.Entity<DemoSession>());
        ConfigureDemoLedgerEntries(builder.Entity<DemoLedgerEntry>());
        ConfigureDemoLedgerTransactions(builder.Entity<DemoLedgerTransaction>());
        ConfigureDemoPositions(builder.Entity<DemoPosition>());
        ConfigureDemoWallets(builder.Entity<DemoWallet>());
        ConfigureExecutionOrders(builder.Entity<ExecutionOrder>());
        ConfigureExecutionOrderTransitions(builder.Entity<ExecutionOrderTransition>());
        ConfigureExecutionTraces(builder.Entity<ExecutionTrace>());
        ConfigureExchangeAccounts(builder.Entity<ExchangeAccount>());
        ConfigureExchangeAccountSyncStates(builder.Entity<ExchangeAccountSyncState>());
        ConfigureExchangeBalances(builder.Entity<ExchangeBalance>());
        ConfigureExchangePositions(builder.Entity<ExchangePosition>());
        ConfigureHealthSnapshots(builder.Entity<HealthSnapshot>());
        ConfigureIncidentEvents(builder.Entity<IncidentEvent>());
        ConfigureIncidents(builder.Entity<Incident>());
        ConfigureGlobalExecutionSwitches(builder.Entity<GlobalExecutionSwitch>());
        ConfigureGlobalSystemStates(builder.Entity<GlobalSystemState>());
        ConfigureSystemStateHistories(builder.Entity<SystemStateHistory>());
        ConfigureHistoricalMarketCandles(builder.Entity<HistoricalMarketCandle>());
        ConfigureMfaEmailOtpChallenges(builder.Entity<MfaEmailOtpChallenge>());
        ConfigureMfaRecoveryCodes(builder.Entity<MfaRecoveryCode>());
        ConfigureMarketScannerCycles(builder.Entity<MarketScannerCycle>());
        ConfigureMarketScannerCandidates(builder.Entity<MarketScannerCandidate>());
        ConfigureMarketScannerHandoffAttempts(builder.Entity<MarketScannerHandoffAttempt>());
        ConfigureRiskPolicies(builder.Entity<RiskPolicy>());
        ConfigureRiskPolicyVersions(builder.Entity<RiskPolicyVersion>());
        ConfigureRiskProfiles(builder.Entity<RiskProfile>());
        ConfigureTradingBots(builder.Entity<TradingBot>());
        ConfigureTradingStrategies(builder.Entity<TradingStrategy>());
        ConfigureTradingStrategySignals(builder.Entity<TradingStrategySignal>());
        ConfigureTradingStrategySignalVetoes(builder.Entity<TradingStrategySignalVeto>());
        ConfigureTradingStrategyVersions(builder.Entity<TradingStrategyVersion>());
        ConfigureWorkerHeartbeats(builder.Entity<WorkerHeartbeat>());
        ConfigureUserExecutionOverrides(builder.Entity<UserExecutionOverride>());
    }

    private string? CurrentUserId => currentUserId;

    private bool HasIsolationBypass => hasIsolationBypass;

    private static void ConfigureApplicationUsers(EntityTypeBuilder<ApplicationUser> builder)
    {
        builder.Property(user => user.PreferredTimeZoneId)
            .HasMaxLength(128);

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

    private static void ConfigureAdminAuditLogs(EntityTypeBuilder<AdminAuditLog> builder)
    {
        builder.ToTable("AdminAuditLogs");
        builder.HasKey(entity => entity.Id);

        builder.Property(entity => entity.ActorUserId)
            .HasMaxLength(450)
            .IsRequired();

        builder.Property(entity => entity.ActionType)
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(entity => entity.TargetType)
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(entity => entity.TargetId)
            .HasMaxLength(256);

        builder.Property(entity => entity.OldValueSummary)
            .HasMaxLength(2048);

        builder.Property(entity => entity.NewValueSummary)
            .HasMaxLength(2048);

        builder.Property(entity => entity.Reason)
            .HasMaxLength(512)
            .IsRequired();

        builder.Property(entity => entity.IpAddress)
            .HasMaxLength(128);

        builder.Property(entity => entity.UserAgent)
            .HasMaxLength(256);

        builder.Property(entity => entity.CorrelationId)
            .HasMaxLength(128);

        builder.HasIndex(entity => new { entity.TargetType, entity.CreatedAtUtc });
        builder.HasIndex(entity => entity.CorrelationId);
    }

    private static void ConfigureAdminCommandRegistryEntries(EntityTypeBuilder<AdminCommandRegistryEntry> builder)
    {
        builder.ToTable("AdminCommandRegistry");
        builder.HasKey(entity => entity.Id);

        builder.Property(entity => entity.CommandId)
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(entity => entity.CommandType)
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(entity => entity.ActorUserId)
            .HasMaxLength(450)
            .IsRequired();

        builder.Property(entity => entity.ScopeKey)
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(entity => entity.PayloadHash)
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(entity => entity.Status)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(entity => entity.ResultSummary)
            .HasMaxLength(512);

        builder.Property(entity => entity.CorrelationId)
            .HasMaxLength(128);

        builder.HasIndex(entity => entity.CommandId)
            .IsUnique();

        builder.HasIndex(entity => new { entity.CommandType, entity.ScopeKey, entity.StartedAtUtc });
    }

    private static void ConfigureApiCredentials(EntityTypeBuilder<ApiCredential> builder)
    {
        builder.ToTable("ApiCredentials");
        builder.HasKey(entity => entity.Id);

        builder.Property(entity => entity.ExchangeAccountId)
            .IsRequired();

        builder.Property(entity => entity.OwnerUserId)
            .HasMaxLength(450)
            .IsRequired();

        builder.Property(entity => entity.ApiKeyCiphertext)
            .HasMaxLength(4096)
            .IsRequired();

        builder.Property(entity => entity.ApiSecretCiphertext)
            .HasMaxLength(4096)
            .IsRequired();

        builder.Property(entity => entity.CredentialFingerprint)
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(entity => entity.KeyVersion)
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(entity => entity.ValidationStatus)
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(entity => entity.PermissionSummary)
            .HasMaxLength(256);

        builder.Property(entity => entity.LastFailureReason)
            .HasMaxLength(512);

        builder.HasIndex(entity => entity.ExchangeAccountId)
            .IsUnique();

        builder.HasIndex(entity => new { entity.ValidationStatus, entity.LastValidatedAtUtc });
    }

    private static void ConfigureApiCredentialValidations(EntityTypeBuilder<ApiCredentialValidation> builder)
    {
        builder.ToTable("ApiCredentialValidations");
        builder.HasKey(entity => entity.Id);

        builder.Property(entity => entity.OwnerUserId)
            .HasMaxLength(450)
            .IsRequired();

        builder.Property(entity => entity.EnvironmentScope)
            .HasMaxLength(32);

        builder.Property(entity => entity.ValidationStatus)
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(entity => entity.PermissionSummary)
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(entity => entity.FailureReason)
            .HasMaxLength(512);

        builder.Property(entity => entity.CorrelationId)
            .HasMaxLength(128);

        builder.HasIndex(entity => new { entity.ExchangeAccountId, entity.ValidatedAtUtc });
        builder.HasIndex(entity => new { entity.ApiCredentialId, entity.ValidatedAtUtc });

        builder.HasOne<ApiCredential>()
            .WithMany()
            .HasForeignKey(entity => entity.ApiCredentialId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureApprovalQueues(EntityTypeBuilder<ApprovalQueue> builder)
    {
        builder.ToTable("ApprovalQueues");
        builder.HasKey(entity => entity.Id);

        builder.Property(entity => entity.ApprovalReference)
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(entity => entity.OperationType)
            .HasConversion<string>()
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(entity => entity.Status)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(entity => entity.Severity)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(entity => entity.Title)
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(entity => entity.Summary)
            .HasMaxLength(512)
            .IsRequired();

        builder.Property(entity => entity.TargetType)
            .HasMaxLength(128);

        builder.Property(entity => entity.TargetId)
            .HasMaxLength(256);

        builder.Property(entity => entity.RequestedByUserId)
            .HasMaxLength(450)
            .IsRequired();

        builder.Property(entity => entity.RequiredApprovals)
            .IsRequired();

        builder.Property(entity => entity.ApprovalCount)
            .IsRequired();

        builder.Property(entity => entity.ExpiresAtUtc)
            .IsRequired();

        builder.Property(entity => entity.Reason)
            .HasMaxLength(512)
            .IsRequired();

        builder.Property(entity => entity.PayloadJson)
            .HasMaxLength(8192)
            .IsRequired();

        builder.Property(entity => entity.PayloadHash)
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(entity => entity.CorrelationId)
            .HasMaxLength(128);

        builder.Property(entity => entity.CommandId)
            .HasMaxLength(128);

        builder.Property(entity => entity.DecisionId)
            .HasMaxLength(64);

        builder.Property(entity => entity.ExecutionAttemptId)
            .HasMaxLength(64);

        builder.Property(entity => entity.IncidentReference)
            .HasMaxLength(128);

        builder.Property(entity => entity.SystemStateHistoryReference)
            .HasMaxLength(128);

        builder.Property(entity => entity.DependencyCircuitBreakerStateReference)
            .HasMaxLength(128);

        builder.Property(entity => entity.RejectReason)
            .HasMaxLength(512);

        builder.Property(entity => entity.ExecutionSummary)
            .HasMaxLength(2048);

        builder.Property(entity => entity.LastActorUserId)
            .HasMaxLength(450);

        builder.HasIndex(entity => entity.ApprovalReference)
            .IsUnique();

        builder.HasIndex(entity => new { entity.Status, entity.ExpiresAtUtc });
        builder.HasIndex(entity => entity.CorrelationId);
        builder.HasIndex(entity => entity.CommandId);
        builder.HasIndex(entity => entity.IncidentReference);
        builder.HasIndex(entity => entity.SystemStateHistoryReference);
    }

    private static void ConfigureApprovalActions(EntityTypeBuilder<ApprovalAction> builder)
    {
        builder.ToTable("ApprovalActions");
        builder.HasKey(entity => entity.Id);

        builder.Property(entity => entity.ApprovalQueueId)
            .IsRequired();

        builder.Property(entity => entity.ApprovalReference)
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(entity => entity.ActionType)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(entity => entity.Sequence)
            .IsRequired();

        builder.Property(entity => entity.ActorUserId)
            .HasMaxLength(450)
            .IsRequired();

        builder.Property(entity => entity.Reason)
            .HasMaxLength(512);

        builder.Property(entity => entity.CorrelationId)
            .HasMaxLength(128);

        builder.Property(entity => entity.CommandId)
            .HasMaxLength(128);

        builder.Property(entity => entity.DecisionId)
            .HasMaxLength(64);

        builder.Property(entity => entity.ExecutionAttemptId)
            .HasMaxLength(64);

        builder.Property(entity => entity.IncidentReference)
            .HasMaxLength(128);

        builder.Property(entity => entity.SystemStateHistoryReference)
            .HasMaxLength(128);

        builder.Property(entity => entity.DependencyCircuitBreakerStateReference)
            .HasMaxLength(128);

        builder.HasIndex(entity => new { entity.ApprovalQueueId, entity.Sequence })
            .IsUnique();

        builder.HasIndex(entity => entity.ApprovalReference);

        builder.HasOne<ApprovalQueue>()
            .WithMany()
            .HasForeignKey(entity => entity.ApprovalQueueId)
            .OnDelete(DeleteBehavior.Cascade);
    }

    private static void ConfigureAutonomyReviewQueueEntries(EntityTypeBuilder<AutonomyReviewQueueEntry> builder)
    {
        builder.ToTable("AutonomyReviewQueue");
        builder.HasKey(entity => entity.Id);

        builder.Property(entity => entity.ApprovalId)
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(entity => entity.ScopeKey)
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(entity => entity.SuggestedAction)
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(entity => entity.ConfidenceScore)
            .HasPrecision(5, 4);

        builder.Property(entity => entity.AffectedUsersCsv)
            .HasMaxLength(2048)
            .IsRequired();

        builder.Property(entity => entity.AffectedSymbolsCsv)
            .HasMaxLength(2048)
            .IsRequired();

        builder.Property(entity => entity.Reason)
            .HasMaxLength(512)
            .IsRequired();

        builder.Property(entity => entity.Status)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(entity => entity.CorrelationId)
            .HasMaxLength(128);

        builder.HasIndex(entity => entity.ApprovalId)
            .IsUnique();

        builder.HasIndex(entity => new { entity.Status, entity.ExpiresAtUtc });
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

        builder.Property(entity => entity.LatestHeartbeatSource)
            .HasMaxLength(64);

        builder.Property(entity => entity.LatestSymbol)
            .HasMaxLength(32);

        builder.Property(entity => entity.LatestTimeframe)
            .HasMaxLength(16);
    }

    private static void ConfigureDependencyCircuitBreakerStates(EntityTypeBuilder<DependencyCircuitBreakerState> builder)
    {
        builder.ToTable("DependencyCircuitBreakerStates");
        builder.HasKey(entity => entity.Id);

        builder.Property(entity => entity.BreakerKind)
            .HasConversion<string>()
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(entity => entity.StateCode)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(entity => entity.LastErrorCode)
            .HasMaxLength(64);

        builder.Property(entity => entity.LastErrorMessage)
            .HasMaxLength(512);

        builder.Property(entity => entity.CorrelationId)
            .HasMaxLength(128);

        builder.HasIndex(entity => entity.BreakerKind)
            .IsUnique();
    }

    private static void ConfigureDecisionTraces(EntityTypeBuilder<DecisionTrace> builder)
    {
        builder.ToTable("DecisionTraces");
        builder.HasKey(entity => entity.Id);

        builder.Property(entity => entity.CorrelationId)
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(entity => entity.DecisionId)
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(entity => entity.UserId)
            .HasMaxLength(450)
            .IsRequired();

        builder.Property(entity => entity.Symbol)
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(entity => entity.Timeframe)
            .HasMaxLength(16)
            .IsRequired();

        builder.Property(entity => entity.StrategyVersion)
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(entity => entity.SignalType)
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(entity => entity.DecisionOutcome)
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(entity => entity.VetoReasonCode)
            .HasMaxLength(64);

        builder.Property(entity => entity.SnapshotJson)
            .HasMaxLength(8192)
            .IsRequired();

        builder.HasIndex(entity => entity.CorrelationId);
        builder.HasIndex(entity => entity.DecisionId)
            .IsUnique();
        builder.HasIndex(entity => new { entity.UserId, entity.CreatedAtUtc });
        builder.HasIndex(entity => entity.StrategySignalId);
    }

    private static void ConfigureIncidentEvents(EntityTypeBuilder<IncidentEvent> builder)
    {
        builder.ToTable("IncidentEvents");
        builder.HasKey(entity => entity.Id);

        builder.Property(entity => entity.IncidentId)
            .IsRequired();

        builder.Property(entity => entity.IncidentReference)
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(entity => entity.EventType)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(entity => entity.Message)
            .HasMaxLength(1024)
            .IsRequired();

        builder.Property(entity => entity.ActorUserId)
            .HasMaxLength(450);

        builder.Property(entity => entity.CorrelationId)
            .HasMaxLength(128);

        builder.Property(entity => entity.CommandId)
            .HasMaxLength(128);

        builder.Property(entity => entity.DecisionId)
            .HasMaxLength(64);

        builder.Property(entity => entity.ExecutionAttemptId)
            .HasMaxLength(64);

        builder.Property(entity => entity.ApprovalReference)
            .HasMaxLength(128);

        builder.Property(entity => entity.SystemStateHistoryReference)
            .HasMaxLength(128);

        builder.Property(entity => entity.DependencyCircuitBreakerStateReference)
            .HasMaxLength(128);

        builder.Property(entity => entity.PayloadJson)
            .HasMaxLength(8192);

        builder.HasIndex(entity => new { entity.IncidentId, entity.CreatedDate });
        builder.HasIndex(entity => entity.IncidentReference);
        builder.HasIndex(entity => entity.CorrelationId);

        builder.HasOne<Incident>()
            .WithMany()
            .HasForeignKey(entity => entity.IncidentId)
            .OnDelete(DeleteBehavior.Cascade);
    }

    private static void ConfigureIncidents(EntityTypeBuilder<Incident> builder)
    {
        builder.ToTable("Incidents");
        builder.HasKey(entity => entity.Id);

        builder.Property(entity => entity.IncidentReference)
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(entity => entity.Severity)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(entity => entity.Status)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(entity => entity.OperationType)
            .HasConversion<string>()
            .HasMaxLength(64);

        builder.Property(entity => entity.Title)
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(entity => entity.Summary)
            .HasMaxLength(512)
            .IsRequired();

        builder.Property(entity => entity.Detail)
            .HasMaxLength(8192)
            .IsRequired();

        builder.Property(entity => entity.TargetType)
            .HasMaxLength(128);

        builder.Property(entity => entity.TargetId)
            .HasMaxLength(256);

        builder.Property(entity => entity.CorrelationId)
            .HasMaxLength(128);

        builder.Property(entity => entity.CommandId)
            .HasMaxLength(128);

        builder.Property(entity => entity.DecisionId)
            .HasMaxLength(64);

        builder.Property(entity => entity.ExecutionAttemptId)
            .HasMaxLength(64);

        builder.Property(entity => entity.ApprovalReference)
            .HasMaxLength(128);

        builder.Property(entity => entity.SystemStateHistoryReference)
            .HasMaxLength(128);

        builder.Property(entity => entity.DependencyCircuitBreakerStateReference)
            .HasMaxLength(128);

        builder.Property(entity => entity.CreatedByUserId)
            .HasMaxLength(450);

        builder.Property(entity => entity.ResolvedByUserId)
            .HasMaxLength(450);

        builder.Property(entity => entity.ResolvedSummary)
            .HasMaxLength(512);

        builder.HasIndex(entity => entity.IncidentReference)
            .IsUnique();

        builder.HasIndex(entity => new { entity.Status, entity.CreatedDate });
        builder.HasIndex(entity => entity.CorrelationId);
        builder.HasIndex(entity => entity.CommandId);
        builder.HasIndex(entity => entity.ApprovalReference);
        builder.HasIndex(entity => new { entity.TargetType, entity.TargetId });
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

    private void ConfigureDemoSessions(EntityTypeBuilder<DemoSession> builder)
    {
        ConfigureUserOwnedEntity(builder, "DemoSessions");

        builder.Property(entity => entity.SequenceNumber)
            .IsRequired();

        builder.Property(entity => entity.SeedAsset)
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(entity => entity.SeedAmount)
            .HasPrecision(38, 18);

        builder.Property(entity => entity.State)
            .HasConversion<string>()
            .HasMaxLength(16)
            .HasDefaultValue(DemoSessionState.Active)
            .IsRequired();

        builder.Property(entity => entity.ConsistencyStatus)
            .HasConversion<string>()
            .HasMaxLength(16)
            .HasDefaultValue(DemoConsistencyStatus.Unknown)
            .IsRequired();

        builder.Property(entity => entity.LastDriftSummary)
            .HasMaxLength(512);

        builder.HasIndex(entity => new { entity.OwnerUserId, entity.SequenceNumber })
            .IsUnique();

        builder.HasIndex(entity => entity.OwnerUserId)
            .HasDatabaseName("IX_DemoSessions_OwnerUserId_Active")
            .HasFilter("[State] = N'Active' AND [IsDeleted] = 0")
            .IsUnique();

        builder.HasIndex(entity => new { entity.OwnerUserId, entity.State, entity.ConsistencyStatus });
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

        builder.Property(entity => entity.ReferenceSymbol)
            .HasMaxLength(32);

        builder.Property(entity => entity.ReferenceQuoteAsset)
            .HasMaxLength(32);

        builder.Property(entity => entity.LastReferencePrice)
            .HasPrecision(38, 18);

        builder.Property(entity => entity.AvailableValueInReferenceQuote)
            .HasPrecision(38, 18);

        builder.Property(entity => entity.ReservedValueInReferenceQuote)
            .HasPrecision(38, 18);

        builder.Property(entity => entity.LastValuationSource)
            .HasMaxLength(64);

        builder.HasIndex(entity => new { entity.OwnerUserId, entity.Asset })
            .IsUnique();
    }

    private void ConfigureExecutionOrders(EntityTypeBuilder<ExecutionOrder> builder)
    {
        ConfigureUserOwnedEntity(builder, "ExecutionOrders");

        builder.Property(entity => entity.SignalType)
            .HasConversion<string>()
            .HasMaxLength(16)
            .IsRequired();

        builder.Property(entity => entity.StrategyKey)
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(entity => entity.Symbol)
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(entity => entity.Timeframe)
            .HasMaxLength(16)
            .IsRequired();

        builder.Property(entity => entity.BaseAsset)
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(entity => entity.QuoteAsset)
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(entity => entity.Side)
            .HasConversion<string>()
            .HasMaxLength(16)
            .IsRequired();

        builder.Property(entity => entity.OrderType)
            .HasConversion<string>()
            .HasMaxLength(16)
            .IsRequired();

        builder.Property(entity => entity.Quantity)
            .HasPrecision(38, 18);

        builder.Property(entity => entity.Price)
            .HasPrecision(38, 18);

        builder.Property(entity => entity.FilledQuantity)
            .HasPrecision(38, 18)
            .HasDefaultValue(0m);

        builder.Property(entity => entity.AverageFillPrice)
            .HasPrecision(38, 18);

        builder.Property(entity => entity.StopLossPrice)
            .HasPrecision(38, 18);

        builder.Property(entity => entity.TakeProfitPrice)
            .HasPrecision(38, 18);

        builder.Property(entity => entity.ExecutionEnvironment)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(entity => entity.ExecutorKind)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(entity => entity.State)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(entity => entity.IdempotencyKey)
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(entity => entity.RootCorrelationId)
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(entity => entity.ParentCorrelationId)
            .HasMaxLength(128);

        builder.Property(entity => entity.ExternalOrderId)
            .HasMaxLength(128);

        builder.Property(entity => entity.FailureCode)
            .HasMaxLength(64);

        builder.Property(entity => entity.FailureDetail)
            .HasMaxLength(512);

        builder.Property(entity => entity.ReconciliationStatus)
            .HasConversion<string>()
            .HasMaxLength(32)
            .HasDefaultValue(ExchangeStateDriftStatus.Unknown)
            .IsRequired();

        builder.Property(entity => entity.ReconciliationSummary)
            .HasMaxLength(512);

        builder.HasIndex(entity => new { entity.OwnerUserId, entity.IdempotencyKey })
            .IsUnique();

        builder.HasIndex(entity => entity.BotId);

        builder.HasIndex(entity => entity.ExchangeAccountId);

        builder.HasIndex(entity => entity.ReplacesExecutionOrderId);

        builder.HasIndex(entity => entity.StrategySignalId);

        builder.HasIndex(entity => new { entity.OwnerUserId, entity.State, entity.LastStateChangedAtUtc });

        builder.HasIndex(entity => new { entity.ExecutorKind, entity.State, entity.LastReconciledAtUtc });
    }

    private void ConfigureExecutionOrderTransitions(EntityTypeBuilder<ExecutionOrderTransition> builder)
    {
        ConfigureUserOwnedEntity(builder, "ExecutionOrderTransitions");

        builder.Property(entity => entity.State)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(entity => entity.EventCode)
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(entity => entity.Detail)
            .HasMaxLength(512);

        builder.Property(entity => entity.CorrelationId)
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(entity => entity.ParentCorrelationId)
            .HasMaxLength(128);

        builder.HasIndex(entity => new { entity.ExecutionOrderId, entity.SequenceNumber })
            .IsUnique();

        builder.HasIndex(entity => new { entity.ExecutionOrderId, entity.OccurredAtUtc });

        builder.HasOne<ExecutionOrder>()
            .WithMany()
            .HasForeignKey(entity => entity.ExecutionOrderId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureExecutionTraces(EntityTypeBuilder<ExecutionTrace> builder)
    {
        builder.ToTable("ExecutionTraces");
        builder.HasKey(entity => entity.Id);

        builder.Property(entity => entity.CorrelationId)
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(entity => entity.ExecutionAttemptId)
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(entity => entity.CommandId)
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(entity => entity.UserId)
            .HasMaxLength(450)
            .IsRequired();

        builder.Property(entity => entity.Provider)
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(entity => entity.Endpoint)
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(entity => entity.RequestMasked)
            .HasMaxLength(4096);

        builder.Property(entity => entity.ResponseMasked)
            .HasMaxLength(4096);

        builder.Property(entity => entity.ExchangeCode)
            .HasMaxLength(64);

        builder.HasIndex(entity => entity.CorrelationId);
        builder.HasIndex(entity => entity.ExecutionAttemptId)
            .IsUnique();
        builder.HasIndex(entity => new { entity.CommandId, entity.CreatedAtUtc });
        builder.HasIndex(entity => new { entity.UserId, entity.CreatedAtUtc });
        builder.HasIndex(entity => entity.ExecutionOrderId);
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

    private static void ConfigureGlobalSystemStates(EntityTypeBuilder<GlobalSystemState> builder)
    {
        builder.ToTable("GlobalSystemStates");
        builder.HasKey(entity => entity.Id);

        builder.Property(entity => entity.State)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(entity => entity.ReasonCode)
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(entity => entity.Message)
            .HasMaxLength(512);

        builder.Property(entity => entity.Source)
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(entity => entity.CorrelationId)
            .HasMaxLength(128);

        builder.Property(entity => entity.UpdatedByUserId)
            .HasMaxLength(450);

        builder.Property(entity => entity.UpdatedFromIp)
            .HasMaxLength(128);

        builder.HasIndex(entity => entity.State);
    }

    private static void ConfigureSystemStateHistories(EntityTypeBuilder<SystemStateHistory> builder)
    {
        builder.ToTable("SystemStateHistories");
        builder.HasKey(entity => entity.Id);

        builder.Property(entity => entity.GlobalSystemStateId)
            .IsRequired();

        builder.Property(entity => entity.HistoryReference)
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(entity => entity.Version)
            .IsRequired();

        builder.Property(entity => entity.State)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(entity => entity.ReasonCode)
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(entity => entity.Message)
            .HasMaxLength(512);

        builder.Property(entity => entity.Source)
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(entity => entity.CorrelationId)
            .HasMaxLength(128);

        builder.Property(entity => entity.CommandId)
            .HasMaxLength(128);

        builder.Property(entity => entity.ApprovalReference)
            .HasMaxLength(128);

        builder.Property(entity => entity.IncidentReference)
            .HasMaxLength(128);

        builder.Property(entity => entity.DependencyCircuitBreakerStateReference)
            .HasMaxLength(128);

        builder.Property(entity => entity.BreakerKind)
            .HasMaxLength(64);

        builder.Property(entity => entity.BreakerStateCode)
            .HasMaxLength(32);

        builder.Property(entity => entity.UpdatedByUserId)
            .HasMaxLength(450);

        builder.Property(entity => entity.UpdatedFromIp)
            .HasMaxLength(128);

        builder.Property(entity => entity.PreviousState)
            .HasMaxLength(32);

        builder.Property(entity => entity.ChangeSummary)
            .HasMaxLength(512);

        builder.HasIndex(entity => entity.HistoryReference)
            .IsUnique();

        builder.HasIndex(entity => new { entity.GlobalSystemStateId, entity.Version })
            .IsUnique();

        builder.HasIndex(entity => entity.CorrelationId);
        builder.HasIndex(entity => entity.CommandId);
        builder.HasIndex(entity => entity.ApprovalReference);
        builder.HasIndex(entity => entity.IncidentReference);

        builder.HasOne<GlobalSystemState>()
            .WithMany()
            .HasForeignKey(entity => entity.GlobalSystemStateId)
            .OnDelete(DeleteBehavior.Restrict);
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

        builder.Property(entity => entity.Symbol)
            .HasMaxLength(32);

        builder.Property(entity => entity.Quantity)
            .HasPrecision(38, 18);

        builder.Property(entity => entity.Leverage)
            .HasPrecision(18, 8);

        builder.Property(entity => entity.MarginType)
            .HasMaxLength(16);

        builder.Property(entity => entity.TradingModeOverride)
            .HasConversion<string>()
            .HasMaxLength(32);

        builder.Property(entity => entity.TradingModeApprovalReference)
            .HasMaxLength(128);

        builder.HasIndex(entity => entity.ExchangeAccountId);
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

    private static void ConfigureMarketScannerCycles(EntityTypeBuilder<MarketScannerCycle> builder)
    {
        builder.ToTable("MarketScannerCycles");
        builder.HasKey(entity => entity.Id);

        builder.Property(entity => entity.UniverseSource)
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(entity => entity.BestCandidateSymbol)
            .HasMaxLength(32);

        builder.Property(entity => entity.BestCandidateScore)
            .HasPrecision(38, 18);

        builder.Property(entity => entity.Summary)
            .HasMaxLength(512);

        builder.HasIndex(entity => entity.CompletedAtUtc);
    }

    private static void ConfigureMarketScannerCandidates(EntityTypeBuilder<MarketScannerCandidate> builder)
    {
        builder.ToTable("MarketScannerCandidates");
        builder.HasKey(entity => entity.Id);

        builder.Property(entity => entity.Symbol)
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(entity => entity.UniverseSource)
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(entity => entity.LastPrice)
            .HasPrecision(38, 18);

        builder.Property(entity => entity.QuoteVolume24h)
            .HasPrecision(38, 18);

        builder.Property(entity => entity.RejectionReason)
            .HasMaxLength(64);

        builder.Property(entity => entity.Score)
            .HasPrecision(38, 18);

        builder.HasIndex(entity => new { entity.ScanCycleId, entity.Rank });
        builder.HasIndex(entity => new { entity.Symbol, entity.ObservedAtUtc });

        builder.HasOne<MarketScannerCycle>()
            .WithMany()
            .HasForeignKey(entity => entity.ScanCycleId)
            .OnDelete(DeleteBehavior.Cascade);
    }
    private static void ConfigureMarketScannerHandoffAttempts(EntityTypeBuilder<MarketScannerHandoffAttempt> builder)
    {
        builder.ToTable("MarketScannerHandoffAttempts");
        builder.HasKey(entity => entity.Id);

        builder.Property(entity => entity.SelectedSymbol)
            .HasMaxLength(32);

        builder.Property(entity => entity.SelectedTimeframe)
            .HasMaxLength(16);

        builder.Property(entity => entity.CandidateScore)
            .HasPrecision(38, 18);

        builder.Property(entity => entity.SelectionReason)
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(entity => entity.OwnerUserId)
            .HasMaxLength(450);

        builder.Property(entity => entity.StrategyKey)
            .HasMaxLength(128);

        builder.Property(entity => entity.StrategyDecisionOutcome)
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(entity => entity.StrategyVetoReasonCode)
            .HasMaxLength(64);

        builder.Property(entity => entity.ExecutionRequestStatus)
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(entity => entity.ExecutionSide)
            .HasConversion<string>()
            .HasMaxLength(16);

        builder.Property(entity => entity.ExecutionOrderType)
            .HasConversion<string>()
            .HasMaxLength(16);

        builder.Property(entity => entity.ExecutionEnvironment)
            .HasConversion<string>()
            .HasMaxLength(32);

        builder.Property(entity => entity.ExecutionQuantity)
            .HasPrecision(38, 18);

        builder.Property(entity => entity.ExecutionPrice)
            .HasPrecision(38, 18);

        builder.Property(entity => entity.BlockerCode)
            .HasMaxLength(64);

        builder.Property(entity => entity.BlockerDetail)
            .HasMaxLength(512);

        builder.Property(entity => entity.GuardSummary)
            .HasMaxLength(512);

        builder.Property(entity => entity.CorrelationId)
            .HasMaxLength(128)
            .IsRequired();

        builder.HasIndex(entity => new { entity.ScanCycleId, entity.CompletedAtUtc });
        builder.HasIndex(entity => new { entity.SelectedSymbol, entity.SelectedAtUtc });
        builder.HasIndex(entity => new { entity.ExecutionRequestStatus, entity.CompletedAtUtc });

        builder.HasOne<MarketScannerCycle>()
            .WithMany()
            .HasForeignKey(entity => entity.ScanCycleId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<MarketScannerCandidate>()
            .WithMany()
            .HasForeignKey(entity => entity.SelectedCandidateId)
            .OnDelete(DeleteBehavior.Restrict);
    }
    private static void ConfigureHealthSnapshots(EntityTypeBuilder<HealthSnapshot> builder)
    {
        builder.ToTable("HealthSnapshots");
        builder.HasKey(entity => entity.Id);

        builder.Property(entity => entity.SnapshotKey)
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(entity => entity.SentinelName)
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(entity => entity.DisplayName)
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(entity => entity.HealthState)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(entity => entity.FreshnessTier)
            .HasConversion<string>()
            .HasMaxLength(16)
            .IsRequired();

        builder.Property(entity => entity.CircuitBreakerState)
            .HasConversion<string>()
            .HasMaxLength(16)
            .IsRequired();

        builder.Property(entity => entity.Detail)
            .HasMaxLength(2048);

        builder.HasIndex(entity => entity.SnapshotKey)
            .IsUnique();

        builder.HasIndex(entity => new { entity.SentinelName, entity.LastUpdatedAtUtc });
    }

    private static void ConfigureRiskPolicies(EntityTypeBuilder<RiskPolicy> builder)
    {
        builder.ToTable("RiskPolicies");
        builder.HasKey(entity => entity.Id);

        builder.Property(entity => entity.PolicyKey)
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(entity => entity.PolicyJson)
            .IsRequired();

        builder.Property(entity => entity.PolicyHash)
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(entity => entity.LastUpdatedByUserId)
            .HasMaxLength(450);

        builder.Property(entity => entity.LastChangeSummary)
            .HasMaxLength(512);

        builder.HasIndex(entity => entity.PolicyKey)
            .IsUnique();
    }

    private static void ConfigureRiskPolicyVersions(EntityTypeBuilder<RiskPolicyVersion> builder)
    {
        builder.ToTable("RiskPolicyVersions");
        builder.HasKey(entity => entity.Id);

        builder.Property(entity => entity.CreatedByUserId)
            .HasMaxLength(450)
            .IsRequired();

        builder.Property(entity => entity.Source)
            .HasMaxLength(128);

        builder.Property(entity => entity.CorrelationId)
            .HasMaxLength(128);

        builder.Property(entity => entity.ChangeSummary)
            .HasMaxLength(512)
            .IsRequired();

        builder.Property(entity => entity.PolicyJson)
            .IsRequired();

        builder.Property(entity => entity.DiffJson);

        builder.HasIndex(entity => new { entity.RiskPolicyId, entity.Version })
            .IsUnique();

        builder.HasIndex(entity => new { entity.RiskPolicyId, entity.CreatedAtUtc });

        builder.HasOne<RiskPolicy>()
            .WithMany()
            .HasForeignKey(entity => entity.RiskPolicyId)
            .OnDelete(DeleteBehavior.Cascade);
    }

    private static void ConfigureWorkerHeartbeats(EntityTypeBuilder<WorkerHeartbeat> builder)
    {
        builder.ToTable("WorkerHeartbeats");
        builder.HasKey(entity => entity.Id);

        builder.Property(entity => entity.WorkerKey)
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(entity => entity.WorkerName)
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(entity => entity.HealthState)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(entity => entity.FreshnessTier)
            .HasConversion<string>()
            .HasMaxLength(16)
            .IsRequired();

        builder.Property(entity => entity.CircuitBreakerState)
            .HasConversion<string>()
            .HasMaxLength(16)
            .IsRequired();

        builder.Property(entity => entity.LastErrorCode)
            .HasMaxLength(64);

        builder.Property(entity => entity.LastErrorMessage)
            .HasMaxLength(1024);

        builder.Property(entity => entity.Detail)
            .HasMaxLength(2048);

        builder.HasIndex(entity => entity.WorkerKey)
            .IsUnique();

        builder.HasIndex(entity => new { entity.WorkerName, entity.LastUpdatedAtUtc });
    }

    private static void ConfigureUserExecutionOverrides(EntityTypeBuilder<UserExecutionOverride> builder)
    {
        builder.ToTable("UserExecutionOverrides");
        builder.HasKey(entity => entity.Id);

        builder.Property(entity => entity.UserId)
            .HasMaxLength(450)
            .IsRequired();

        builder.Property(entity => entity.AllowedSymbolsCsv)
            .HasMaxLength(512);

        builder.Property(entity => entity.DeniedSymbolsCsv)
            .HasMaxLength(512);

        builder.Property(entity => entity.LeverageCap)
            .HasPrecision(18, 8);

        builder.Property(entity => entity.MaxOrderSize)
            .HasPrecision(38, 18);

        builder.HasIndex(entity => entity.UserId)
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

    private void ApplyAppendOnlyRules()
    {
        foreach (var entry in ChangeTracker.Entries<AdminAuditLog>())
        {
            if (entry.State is EntityState.Modified or EntityState.Deleted)
            {
                throw new InvalidOperationException("AdminAuditLog is append-only and cannot be updated or deleted.");
            }
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


