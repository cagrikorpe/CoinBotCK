using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoinBot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGovernanceIncidentApprovalVersioningFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ApprovalQueues",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ApprovalReference = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    OperationType = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Severity = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Summary = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    TargetType = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    TargetId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    RequestedByUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    RequiredApprovals = table.Column<int>(type: "int", nullable: false),
                    ApprovalCount = table.Column<int>(type: "int", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    PayloadJson = table.Column<string>(type: "nvarchar(max)", maxLength: 8192, nullable: false),
                    PayloadHash = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    CorrelationId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    CommandId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    DecisionId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ExecutionAttemptId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    IncidentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IncidentReference = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    SystemStateHistoryId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SystemStateHistoryReference = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    DependencyCircuitBreakerStateId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DependencyCircuitBreakerStateReference = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    ApprovedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ExecutedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RejectedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ExpiredAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RejectReason = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    ExecutionSummary = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: true),
                    LastActorUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApprovalQueues", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AutonomyReviewQueue",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ApprovalId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ScopeKey = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    SuggestedAction = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ConfidenceScore = table.Column<decimal>(type: "decimal(5,4)", precision: 5, scale: 4, nullable: false),
                    AffectedUsersCsv = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false),
                    AffectedSymbolsCsv = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    CorrelationId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutonomyReviewQueue", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DependencyCircuitBreakerStates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BreakerKind = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    StateCode = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ConsecutiveFailureCount = table.Column<int>(type: "int", nullable: false),
                    LastFailureAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastSuccessAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CooldownUntilUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    HalfOpenStartedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastProbeAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastErrorCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    LastErrorMessage = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DependencyCircuitBreakerStates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Incidents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IncidentReference = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Severity = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    OperationType = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Title = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Summary = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    Detail = table.Column<string>(type: "nvarchar(max)", maxLength: 8192, nullable: false),
                    TargetType = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    TargetId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    CommandId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    DecisionId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ExecutionAttemptId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ApprovalQueueId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ApprovalReference = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    SystemStateHistoryId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SystemStateHistoryReference = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    DependencyCircuitBreakerStateId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DependencyCircuitBreakerStateReference = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    CreatedByUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    ResolvedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ResolvedByUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    ResolvedSummary = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Incidents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SystemStateHistories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    GlobalSystemStateId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    HistoryReference = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    State = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ReasonCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Message = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    Source = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    IsManualOverride = table.Column<bool>(type: "bit", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    CommandId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    ApprovalQueueId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ApprovalReference = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    IncidentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IncidentReference = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    DependencyCircuitBreakerStateId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DependencyCircuitBreakerStateReference = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    BreakerKind = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    BreakerStateCode = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    UpdatedByUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    UpdatedFromIp = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    PreviousState = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    ChangeSummary = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SystemStateHistories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SystemStateHistories_GlobalSystemStates_GlobalSystemStateId",
                        column: x => x.GlobalSystemStateId,
                        principalTable: "GlobalSystemStates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ApprovalActions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ApprovalQueueId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ApprovalReference = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ActionType = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Sequence = table.Column<int>(type: "int", nullable: false),
                    ActorUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    CommandId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    DecisionId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ExecutionAttemptId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    IncidentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IncidentReference = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    SystemStateHistoryId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SystemStateHistoryReference = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    DependencyCircuitBreakerStateId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DependencyCircuitBreakerStateReference = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApprovalActions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ApprovalActions_ApprovalQueues_ApprovalQueueId",
                        column: x => x.ApprovalQueueId,
                        principalTable: "ApprovalQueues",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "IncidentEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IncidentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IncidentReference = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    EventType = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Message = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: false),
                    ActorUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    CommandId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    DecisionId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ExecutionAttemptId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ApprovalQueueId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ApprovalReference = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    SystemStateHistoryId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SystemStateHistoryReference = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    DependencyCircuitBreakerStateId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DependencyCircuitBreakerStateReference = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    PayloadJson = table.Column<string>(type: "nvarchar(max)", maxLength: 8192, nullable: true),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IncidentEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IncidentEvents_Incidents_IncidentId",
                        column: x => x.IncidentId,
                        principalTable: "Incidents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalActions_ApprovalQueueId_Sequence",
                table: "ApprovalActions",
                columns: new[] { "ApprovalQueueId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalActions_ApprovalReference",
                table: "ApprovalActions",
                column: "ApprovalReference");

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalQueues_ApprovalReference",
                table: "ApprovalQueues",
                column: "ApprovalReference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalQueues_CommandId",
                table: "ApprovalQueues",
                column: "CommandId");

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalQueues_CorrelationId",
                table: "ApprovalQueues",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalQueues_IncidentReference",
                table: "ApprovalQueues",
                column: "IncidentReference");

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalQueues_Status_ExpiresAtUtc",
                table: "ApprovalQueues",
                columns: new[] { "Status", "ExpiresAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalQueues_SystemStateHistoryReference",
                table: "ApprovalQueues",
                column: "SystemStateHistoryReference");

            migrationBuilder.CreateIndex(
                name: "IX_AutonomyReviewQueue_ApprovalId",
                table: "AutonomyReviewQueue",
                column: "ApprovalId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AutonomyReviewQueue_Status_ExpiresAtUtc",
                table: "AutonomyReviewQueue",
                columns: new[] { "Status", "ExpiresAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_DependencyCircuitBreakerStates_BreakerKind",
                table: "DependencyCircuitBreakerStates",
                column: "BreakerKind",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IncidentEvents_CorrelationId",
                table: "IncidentEvents",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_IncidentEvents_IncidentId_CreatedDate",
                table: "IncidentEvents",
                columns: new[] { "IncidentId", "CreatedDate" });

            migrationBuilder.CreateIndex(
                name: "IX_IncidentEvents_IncidentReference",
                table: "IncidentEvents",
                column: "IncidentReference");

            migrationBuilder.CreateIndex(
                name: "IX_Incidents_ApprovalReference",
                table: "Incidents",
                column: "ApprovalReference");

            migrationBuilder.CreateIndex(
                name: "IX_Incidents_CommandId",
                table: "Incidents",
                column: "CommandId");

            migrationBuilder.CreateIndex(
                name: "IX_Incidents_CorrelationId",
                table: "Incidents",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_Incidents_IncidentReference",
                table: "Incidents",
                column: "IncidentReference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Incidents_Status_CreatedDate",
                table: "Incidents",
                columns: new[] { "Status", "CreatedDate" });

            migrationBuilder.CreateIndex(
                name: "IX_Incidents_TargetType_TargetId",
                table: "Incidents",
                columns: new[] { "TargetType", "TargetId" });

            migrationBuilder.CreateIndex(
                name: "IX_SystemStateHistories_ApprovalReference",
                table: "SystemStateHistories",
                column: "ApprovalReference");

            migrationBuilder.CreateIndex(
                name: "IX_SystemStateHistories_CommandId",
                table: "SystemStateHistories",
                column: "CommandId");

            migrationBuilder.CreateIndex(
                name: "IX_SystemStateHistories_CorrelationId",
                table: "SystemStateHistories",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_SystemStateHistories_GlobalSystemStateId_Version",
                table: "SystemStateHistories",
                columns: new[] { "GlobalSystemStateId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SystemStateHistories_HistoryReference",
                table: "SystemStateHistories",
                column: "HistoryReference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SystemStateHistories_IncidentReference",
                table: "SystemStateHistories",
                column: "IncidentReference");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ApprovalActions");

            migrationBuilder.DropTable(
                name: "AutonomyReviewQueue");

            migrationBuilder.DropTable(
                name: "DependencyCircuitBreakerStates");

            migrationBuilder.DropTable(
                name: "IncidentEvents");

            migrationBuilder.DropTable(
                name: "SystemStateHistories");

            migrationBuilder.DropTable(
                name: "ApprovalQueues");

            migrationBuilder.DropTable(
                name: "Incidents");
        }
    }
}
