using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoinBot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMarketScannerHandoffAttempts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MarketScannerHandoffAttempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ScanCycleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SelectedCandidateId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SelectedSymbol = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    SelectedTimeframe = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    SelectedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CandidateRank = table.Column<int>(type: "int", nullable: true),
                    CandidateScore = table.Column<decimal>(type: "decimal(38,18)", precision: 38, scale: 18, nullable: true),
                    SelectionReason = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    OwnerUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    BotId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    StrategyKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    TradingStrategyId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TradingStrategyVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    StrategySignalId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    StrategySignalVetoId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    StrategyDecisionOutcome = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    StrategyVetoReasonCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    StrategyScore = table.Column<int>(type: "int", nullable: true),
                    ExecutionRequestStatus = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ExecutionSide = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    ExecutionOrderType = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    ExecutionEnvironment = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    ExecutionQuantity = table.Column<decimal>(type: "decimal(38,18)", precision: 38, scale: 18, nullable: true),
                    ExecutionPrice = table.Column<decimal>(type: "decimal(38,18)", precision: 38, scale: 18, nullable: true),
                    BlockerCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    BlockerDetail = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    GuardSummary = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MarketScannerHandoffAttempts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MarketScannerHandoffAttempts_MarketScannerCandidates_SelectedCandidateId",
                        column: x => x.SelectedCandidateId,
                        principalTable: "MarketScannerCandidates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MarketScannerHandoffAttempts_MarketScannerCycles_ScanCycleId",
                        column: x => x.ScanCycleId,
                        principalTable: "MarketScannerCycles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MarketScannerHandoffAttempts_ExecutionRequestStatus_CompletedAtUtc",
                table: "MarketScannerHandoffAttempts",
                columns: new[] { "ExecutionRequestStatus", "CompletedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_MarketScannerHandoffAttempts_ScanCycleId_CompletedAtUtc",
                table: "MarketScannerHandoffAttempts",
                columns: new[] { "ScanCycleId", "CompletedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_MarketScannerHandoffAttempts_SelectedCandidateId",
                table: "MarketScannerHandoffAttempts",
                column: "SelectedCandidateId");

            migrationBuilder.CreateIndex(
                name: "IX_MarketScannerHandoffAttempts_SelectedSymbol_SelectedAtUtc",
                table: "MarketScannerHandoffAttempts",
                columns: new[] { "SelectedSymbol", "SelectedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MarketScannerHandoffAttempts");
        }
    }
}
