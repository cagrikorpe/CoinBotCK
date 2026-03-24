using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoinBot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMonitoringAndPolicyControlPlaneFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "HealthSnapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SnapshotKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    SentinelName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    HealthState = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    FreshnessTier = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    CircuitBreakerState = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    LastUpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ObservedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    BinancePingMs = table.Column<int>(type: "int", nullable: true),
                    WebSocketStaleDurationSeconds = table.Column<int>(type: "int", nullable: true),
                    LastMessageAgeSeconds = table.Column<int>(type: "int", nullable: true),
                    ReconnectCount = table.Column<int>(type: "int", nullable: true),
                    StreamGapCount = table.Column<int>(type: "int", nullable: true),
                    RateLimitUsage = table.Column<int>(type: "int", nullable: true),
                    DbLatencyMs = table.Column<int>(type: "int", nullable: true),
                    RedisLatencyMs = table.Column<int>(type: "int", nullable: true),
                    SignalRActiveConnectionCount = table.Column<int>(type: "int", nullable: true),
                    WorkerLastHeartbeatAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ConsecutiveFailureCount = table.Column<int>(type: "int", nullable: true),
                    SnapshotAgeSeconds = table.Column<int>(type: "int", nullable: true),
                    Detail = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HealthSnapshots", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RiskPolicies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PolicyKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    CurrentVersion = table.Column<int>(type: "int", nullable: false),
                    PolicyJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    PolicyHash = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    LastUpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastUpdatedByUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    LastChangeSummary = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RiskPolicies", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WorkerHeartbeats",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkerKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    WorkerName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    HealthState = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    FreshnessTier = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    CircuitBreakerState = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    LastHeartbeatAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastUpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ConsecutiveFailureCount = table.Column<int>(type: "int", nullable: false),
                    LastErrorCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    LastErrorMessage = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                    SnapshotAgeSeconds = table.Column<int>(type: "int", nullable: true),
                    Detail = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkerHeartbeats", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RiskPolicyVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RiskPolicyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedByUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Source = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    ChangeSummary = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    PolicyJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    DiffJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RolledBackFromVersion = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RiskPolicyVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RiskPolicyVersions_RiskPolicies_RiskPolicyId",
                        column: x => x.RiskPolicyId,
                        principalTable: "RiskPolicies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_HealthSnapshots_SentinelName_LastUpdatedAtUtc",
                table: "HealthSnapshots",
                columns: new[] { "SentinelName", "LastUpdatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_HealthSnapshots_SnapshotKey",
                table: "HealthSnapshots",
                column: "SnapshotKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RiskPolicies_PolicyKey",
                table: "RiskPolicies",
                column: "PolicyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RiskPolicyVersions_RiskPolicyId_CreatedAtUtc",
                table: "RiskPolicyVersions",
                columns: new[] { "RiskPolicyId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_RiskPolicyVersions_RiskPolicyId_Version",
                table: "RiskPolicyVersions",
                columns: new[] { "RiskPolicyId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkerHeartbeats_WorkerKey",
                table: "WorkerHeartbeats",
                column: "WorkerKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkerHeartbeats_WorkerName_LastUpdatedAtUtc",
                table: "WorkerHeartbeats",
                columns: new[] { "WorkerName", "LastUpdatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HealthSnapshots");

            migrationBuilder.DropTable(
                name: "RiskPolicyVersions");

            migrationBuilder.DropTable(
                name: "WorkerHeartbeats");

            migrationBuilder.DropTable(
                name: "RiskPolicies");
        }
    }
}
