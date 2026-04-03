using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoinBot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMarketScannerInfra : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MarketScannerCycles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UniverseSource = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    ScannedSymbolCount = table.Column<int>(type: "int", nullable: false),
                    EligibleCandidateCount = table.Column<int>(type: "int", nullable: false),
                    TopCandidateCount = table.Column<int>(type: "int", nullable: false),
                    BestCandidateSymbol = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    BestCandidateScore = table.Column<decimal>(type: "decimal(38,18)", precision: 38, scale: 18, nullable: true),
                    Summary = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MarketScannerCycles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MarketScannerCandidates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ScanCycleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Symbol = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    UniverseSource = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    ObservedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastCandleAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastPrice = table.Column<decimal>(type: "decimal(38,18)", precision: 38, scale: 18, nullable: true),
                    QuoteVolume24h = table.Column<decimal>(type: "decimal(38,18)", precision: 38, scale: 18, nullable: true),
                    IsEligible = table.Column<bool>(type: "bit", nullable: false),
                    RejectionReason = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Score = table.Column<decimal>(type: "decimal(38,18)", precision: 38, scale: 18, nullable: false),
                    Rank = table.Column<int>(type: "int", nullable: true),
                    IsTopCandidate = table.Column<bool>(type: "bit", nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MarketScannerCandidates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MarketScannerCandidates_MarketScannerCycles_ScanCycleId",
                        column: x => x.ScanCycleId,
                        principalTable: "MarketScannerCycles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MarketScannerCandidates_ScanCycleId_Rank",
                table: "MarketScannerCandidates",
                columns: new[] { "ScanCycleId", "Rank" });

            migrationBuilder.CreateIndex(
                name: "IX_MarketScannerCandidates_Symbol_ObservedAtUtc",
                table: "MarketScannerCandidates",
                columns: new[] { "Symbol", "ObservedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_MarketScannerCycles_CompletedAtUtc",
                table: "MarketScannerCycles",
                column: "CompletedAtUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MarketScannerCandidates");

            migrationBuilder.DropTable(
                name: "MarketScannerCycles");
        }
    }
}
