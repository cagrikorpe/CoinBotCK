using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoinBot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDemoConsistencyWatchdogAndSessionResetControls : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "AverageFillPrice",
                table: "ExecutionOrders",
                type: "decimal(38,18)",
                precision: 38,
                scale: 18,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "FilledQuantity",
                table: "ExecutionOrders",
                type: "decimal(38,18)",
                precision: 38,
                scale: 18,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastDriftDetectedAtUtc",
                table: "ExecutionOrders",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastFilledAtUtc",
                table: "ExecutionOrders",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastReconciledAtUtc",
                table: "ExecutionOrders",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReconciliationStatus",
                table: "ExecutionOrders",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Unknown");

            migrationBuilder.AddColumn<string>(
                name: "ReconciliationSummary",
                table: "ExecutionOrders",
                type: "nvarchar(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ReplacesExecutionOrderId",
                table: "ExecutionOrders",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "StopLossPrice",
                table: "ExecutionOrders",
                type: "decimal(38,18)",
                precision: 38,
                scale: 18,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "TakeProfitPrice",
                table: "ExecutionOrders",
                type: "decimal(38,18)",
                precision: 38,
                scale: 18,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "DemoSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SequenceNumber = table.Column<int>(type: "int", nullable: false),
                    SeedAsset = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    SeedAmount = table.Column<decimal>(type: "decimal(38,18)", precision: 38, scale: 18, nullable: false),
                    State = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false, defaultValue: "Active"),
                    ConsistencyStatus = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false, defaultValue: "Unknown"),
                    StartedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ClosedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastConsistencyCheckedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastDriftDetectedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastDriftSummary = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    OwnerUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DemoSessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DemoSessions_AspNetUsers_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ExecutionOrders_ExecutorKind_State_LastReconciledAtUtc",
                table: "ExecutionOrders",
                columns: new[] { "ExecutorKind", "State", "LastReconciledAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ExecutionOrders_ReplacesExecutionOrderId",
                table: "ExecutionOrders",
                column: "ReplacesExecutionOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_DemoSessions_OwnerUserId_Active",
                table: "DemoSessions",
                column: "OwnerUserId",
                unique: true,
                filter: "[State] = N'Active' AND [IsDeleted] = 0");

            migrationBuilder.CreateIndex(
                name: "IX_DemoSessions_OwnerUserId_SequenceNumber",
                table: "DemoSessions",
                columns: new[] { "OwnerUserId", "SequenceNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DemoSessions_OwnerUserId_State_ConsistencyStatus",
                table: "DemoSessions",
                columns: new[] { "OwnerUserId", "State", "ConsistencyStatus" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DemoSessions");

            migrationBuilder.DropIndex(
                name: "IX_ExecutionOrders_ExecutorKind_State_LastReconciledAtUtc",
                table: "ExecutionOrders");

            migrationBuilder.DropIndex(
                name: "IX_ExecutionOrders_ReplacesExecutionOrderId",
                table: "ExecutionOrders");

            migrationBuilder.DropColumn(
                name: "AverageFillPrice",
                table: "ExecutionOrders");

            migrationBuilder.DropColumn(
                name: "FilledQuantity",
                table: "ExecutionOrders");

            migrationBuilder.DropColumn(
                name: "LastDriftDetectedAtUtc",
                table: "ExecutionOrders");

            migrationBuilder.DropColumn(
                name: "LastFilledAtUtc",
                table: "ExecutionOrders");

            migrationBuilder.DropColumn(
                name: "LastReconciledAtUtc",
                table: "ExecutionOrders");

            migrationBuilder.DropColumn(
                name: "ReconciliationStatus",
                table: "ExecutionOrders");

            migrationBuilder.DropColumn(
                name: "ReconciliationSummary",
                table: "ExecutionOrders");

            migrationBuilder.DropColumn(
                name: "ReplacesExecutionOrderId",
                table: "ExecutionOrders");

            migrationBuilder.DropColumn(
                name: "StopLossPrice",
                table: "ExecutionOrders");

            migrationBuilder.DropColumn(
                name: "TakeProfitPrice",
                table: "ExecutionOrders");
        }
    }
}
