using System.Text.Json;
using CoinBot.Application.Abstractions.Administration;
using CoinBot.Application.Abstractions.Auditing;
using CoinBot.Application.Abstractions.DataScope;
using CoinBot.Application.Abstractions.Policy;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Administration;
using CoinBot.Infrastructure.Persistence;
using CoinBot.UnitTests.Infrastructure.Mfa;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace CoinBot.UnitTests.Infrastructure.Administration;

public sealed class ApprovalWorkflowServiceTests
{
    [Fact]
    public async Task EnqueueAsync_CreatesIncidentQueueAndAuditTrail()
    {
        await using var harness = CreateHarness();
        var utcNow = harness.TimeProvider.GetUtcNow().UtcDateTime;
        var payloadJson = CreateStatePayload("cmd-approval-1", utcNow);

        var detail = await harness.Service.EnqueueAsync(
            new ApprovalQueueEnqueueRequest(
                ApprovalQueueOperationType.GlobalSystemStateUpdate,
                IncidentSeverity.Warning,
                "Global system state update",
                "Maintenance request",
                "requestor-1",
                "Planned maintenance",
                payloadJson,
                2,
                utcNow.AddHours(8),
                "GlobalSystemState",
                "Singleton",
                "corr-approval-1",
                "cmd-approval-1"),
            CancellationToken.None);

        Assert.Equal(ApprovalQueueStatus.Pending, detail.Status);
        Assert.Equal(2, detail.RequiredApprovals);
        Assert.Single(harness.DbContext.ApprovalQueues);
        Assert.Single(harness.DbContext.Incidents);
        Assert.Equal(2, await harness.DbContext.IncidentEvents.CountAsync());
        Assert.Single(harness.AdminAuditLogService.Requests);
        Assert.Equal("Admin.Approval.Queue.Enqueue", harness.AdminAuditLogService.Requests[0].ActionType);
    }

    [Fact]
    public async Task ApproveAsync_RequiresDistinctApprovers_AndFinalApprovalExecutesStateUpdate()
    {
        await using var harness = CreateHarness();
        var utcNow = harness.TimeProvider.GetUtcNow().UtcDateTime;
        var payloadJson = CreateStatePayload("cmd-approval-2", utcNow);

        var queued = await harness.Service.EnqueueAsync(
            new ApprovalQueueEnqueueRequest(
                ApprovalQueueOperationType.GlobalSystemStateUpdate,
                IncidentSeverity.Warning,
                "Global system state update",
                "Maintenance request",
                "requestor-1",
                "Planned maintenance",
                payloadJson,
                2,
                utcNow.AddHours(8),
                "GlobalSystemState",
                "Singleton",
                "corr-approval-2",
                "cmd-approval-2"),
            CancellationToken.None);

        var firstApproval = await harness.Service.ApproveAsync(
            new ApprovalQueueDecisionRequest(
                queued.ApprovalReference,
                "approver-1",
                null,
                "corr-approval-2"),
            CancellationToken.None);

        Assert.Equal(ApprovalQueueStatus.Pending, firstApproval.Status);
        Assert.Equal(1, firstApproval.ApprovalCount);

        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Service.ApproveAsync(
            new ApprovalQueueDecisionRequest(
                queued.ApprovalReference,
                "approver-1",
                "second attempt",
                "corr-approval-2"),
            CancellationToken.None));

        var finalApproval = await harness.Service.ApproveAsync(
            new ApprovalQueueDecisionRequest(
                queued.ApprovalReference,
                "approver-2",
                "Approved by second approver",
                "corr-approval-2"),
            CancellationToken.None);

        Assert.Equal(ApprovalQueueStatus.Executed, finalApproval.Status);
        Assert.Equal(2, finalApproval.ApprovalCount);
        Assert.NotNull(finalApproval.ExecutedAtUtc);
        Assert.Single(harness.DbContext.SystemStateHistories);
        var history = await harness.DbContext.SystemStateHistories.SingleAsync();
        Assert.Equal(1, history.Version);
        Assert.Equal("cmd-approval-2", history.CommandId);
        Assert.Equal(finalApproval.ApprovalReference, history.ApprovalReference);
        Assert.Equal("PLANNED_MAINTENANCE", history.ReasonCode);
        Assert.Equal("Active", history.PreviousState);
        Assert.Single(harness.CommandRegistry.CompletionRequests);
        Assert.Equal(AdminCommandStatus.Completed, harness.CommandRegistry.CompletionRequests[0].Status);
    }

    [Fact]
    public async Task RejectAsync_RequiresReason_AndWritesRejectedOutcome()
    {
        await using var harness = CreateHarness();
        var utcNow = harness.TimeProvider.GetUtcNow().UtcDateTime;
        var payloadJson = CreateStatePayload("cmd-approval-3", utcNow);

        var queued = await harness.Service.EnqueueAsync(
            new ApprovalQueueEnqueueRequest(
                ApprovalQueueOperationType.GlobalPolicyRollback,
                IncidentSeverity.Critical,
                "Global policy rollback",
                "Rollback request",
                "requestor-1",
                "Policy rollback",
                payloadJson,
                2,
                utcNow.AddHours(8),
                "RiskPolicy",
                "GlobalRiskPolicy",
                "corr-approval-3",
                "cmd-approval-3"),
            CancellationToken.None);

        await Assert.ThrowsAsync<ArgumentException>(() => harness.Service.RejectAsync(
            new ApprovalQueueDecisionRequest(
                queued.ApprovalReference,
                "approver-3",
                "   ",
                "corr-approval-3"),
            CancellationToken.None));

        var rejected = await harness.Service.RejectAsync(
            new ApprovalQueueDecisionRequest(
                queued.ApprovalReference,
                "approver-3",
                "Rejected because the change window is closed.",
                "corr-approval-3"),
            CancellationToken.None);

        Assert.Equal(ApprovalQueueStatus.Rejected, rejected.Status);
        Assert.Equal("Rejected because the change window is closed.", rejected.RejectReason);
        Assert.Single(harness.CommandRegistry.CompletionRequests);
        Assert.Equal(AdminCommandStatus.Failed, harness.CommandRegistry.CompletionRequests[0].Status);
        Assert.Single(harness.AdminAuditLogService.Requests, request => request.ActionType == "Admin.Approval.Queue.Rejected");
    }

    [Fact]
    public async Task ExpirePendingAsync_MarksPendingQueueExpired()
    {
        await using var harness = CreateHarness();
        var utcNow = harness.TimeProvider.GetUtcNow().UtcDateTime;
        var payloadJson = CreateStatePayload("cmd-approval-4", utcNow);

        var queued = await harness.Service.EnqueueAsync(
            new ApprovalQueueEnqueueRequest(
                ApprovalQueueOperationType.GlobalSystemStateUpdate,
                IncidentSeverity.Warning,
                "Global system state update",
                "Maintenance request",
                "requestor-1",
                "Planned maintenance",
                payloadJson,
                2,
                utcNow.AddHours(1),
                "GlobalSystemState",
                "Singleton",
                "corr-approval-4",
                "cmd-approval-4"),
            CancellationToken.None);

        harness.TimeProvider.Advance(TimeSpan.FromHours(2));

        var expiredCount = await harness.Service.ExpirePendingAsync(CancellationToken.None);
        var detail = await harness.Service.GetDetailAsync(queued.ApprovalReference, CancellationToken.None);

        Assert.Equal(1, expiredCount);
        Assert.NotNull(detail);
        Assert.Equal(ApprovalQueueStatus.Expired, detail.Status);
        Assert.NotNull(detail.ExpiredAtUtc);
        Assert.Single(harness.CommandRegistry.CompletionRequests);
        Assert.Equal(AdminCommandStatus.Failed, harness.CommandRegistry.CompletionRequests[0].Status);
        Assert.Single(harness.AdminAuditLogService.Requests, request => request.ActionType == "Admin.Approval.Queue.Expired");
    }

    private static string CreateStatePayload(string commandId, DateTime utcNow)
    {
        return JsonSerializer.Serialize(
            new GlobalSystemStateSetRequest(
                GlobalSystemStateKind.Maintenance,
                "PLANNED_MAINTENANCE",
                "Maintenance window",
                "AdminPortal.Settings",
                "corr-payload",
                IsManualOverride: true,
                ExpiresAtUtc: utcNow.AddHours(8),
                UpdatedByUserId: "super-admin",
                UpdatedFromIp: "ip:masked",
                CommandId: commandId,
                ChangeSummary: "Maintenance window"),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    private static TestHarness CreateHarness()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        var dbContext = new ApplicationDbContext(options, new TestDataScopeContext());
        var auditLogService = new FakeAuditLogService();
        var adminAuditLogService = new FakeAdminAuditLogService();
        var commandRegistry = new FakeAdminCommandRegistry();
        var timeProvider = new AdjustableTimeProvider(new DateTimeOffset(2026, 3, 24, 12, 0, 0, TimeSpan.Zero));
        var stateService = new GlobalSystemStateService(dbContext, auditLogService, timeProvider);
        var policyEngine = new FakeGlobalPolicyEngine();
        var service = new ApprovalWorkflowService(
            dbContext,
            adminAuditLogService,
            commandRegistry,
            stateService,
            policyEngine,
            timeProvider,
            NullLogger<ApprovalWorkflowService>.Instance);

        return new TestHarness(dbContext, service, auditLogService, adminAuditLogService, commandRegistry, timeProvider);
    }

    private sealed class TestDataScopeContext : IDataScopeContext
    {
        public string? UserId => null;

        public bool HasIsolationBypass => true;
    }

    private sealed class FakeAuditLogService : IAuditLogService
    {
        public List<AuditLogWriteRequest> Requests { get; } = [];

        public Task WriteAsync(AuditLogWriteRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeAdminAuditLogService : IAdminAuditLogService
    {
        public List<AdminAuditLogWriteRequest> Requests { get; } = [];

        public Task WriteAsync(AdminAuditLogWriteRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeAdminCommandRegistry : IAdminCommandRegistry
    {
        public AdminCommandStartResult StartResult { get; set; } = new(
            AdminCommandStartDisposition.Started,
            AdminCommandStatus.Running,
            ResultSummary: null);

        public List<AdminCommandCompletionRequest> CompletionRequests { get; } = [];

        public Task<AdminCommandStartResult> TryStartAsync(AdminCommandStartRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(StartResult);
        }

        public Task CompleteAsync(AdminCommandCompletionRequest request, CancellationToken cancellationToken = default)
        {
            CompletionRequests.Add(request);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeGlobalPolicyEngine : IGlobalPolicyEngine
    {
        public GlobalPolicySnapshot Snapshot { get; set; } = GlobalPolicySnapshot.CreateDefault(new DateTime(2026, 3, 24, 12, 0, 0, DateTimeKind.Utc));

        public Task<GlobalPolicySnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Snapshot);
        }

        public Task<GlobalPolicyEvaluationResult> EvaluateAsync(GlobalPolicyEvaluationRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new GlobalPolicyEvaluationResult(false, null, null, Snapshot.CurrentVersion, null, Snapshot.Policy.AutonomyPolicy.Mode));
        }

        public Task<GlobalPolicySnapshot> UpdateAsync(GlobalPolicyUpdateRequest request, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<GlobalPolicySnapshot> RollbackAsync(GlobalPolicyRollbackRequest request, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class TestHarness(
        ApplicationDbContext dbContext,
        ApprovalWorkflowService service,
        FakeAuditLogService auditLogService,
        FakeAdminAuditLogService adminAuditLogService,
        FakeAdminCommandRegistry commandRegistry,
        AdjustableTimeProvider timeProvider) : IAsyncDisposable
    {
        public ApplicationDbContext DbContext { get; } = dbContext;

        public ApprovalWorkflowService Service { get; } = service;

        public FakeAuditLogService AuditLogService { get; } = auditLogService;

        public FakeAdminAuditLogService AdminAuditLogService { get; } = adminAuditLogService;

        public FakeAdminCommandRegistry CommandRegistry { get; } = commandRegistry;

        public AdjustableTimeProvider TimeProvider { get; } = timeProvider;

        public async ValueTask DisposeAsync()
        {
            await DbContext.DisposeAsync();
        }
    }
}
