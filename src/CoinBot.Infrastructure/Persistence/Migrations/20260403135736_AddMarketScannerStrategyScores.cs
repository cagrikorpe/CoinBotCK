using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoinBot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMarketScannerStrategyScores : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "SelectionReason",
                table: "MarketScannerHandoffAttempts",
                type: "nvarchar(512)",
                maxLength: 512,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(256)",
                oldMaxLength: 256);

            migrationBuilder.AddColumn<decimal>(
                name: "CandidateMarketScore",
                table: "MarketScannerHandoffAttempts",
                type: "decimal(38,18)",
                precision: 38,
                scale: 18,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "MarketScore",
                table: "MarketScannerCandidates",
                type: "decimal(38,18)",
                precision: 38,
                scale: 18,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "ScoringSummary",
                table: "MarketScannerCandidates",
                type: "nvarchar(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "StrategyScore",
                table: "MarketScannerCandidates",
                type: "int",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CandidateMarketScore",
                table: "MarketScannerHandoffAttempts");

            migrationBuilder.DropColumn(
                name: "MarketScore",
                table: "MarketScannerCandidates");

            migrationBuilder.DropColumn(
                name: "ScoringSummary",
                table: "MarketScannerCandidates");

            migrationBuilder.DropColumn(
                name: "StrategyScore",
                table: "MarketScannerCandidates");

            migrationBuilder.AlterColumn<string>(
                name: "SelectionReason",
                table: "MarketScannerHandoffAttempts",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(512)",
                oldMaxLength: 512);
        }
    }
}
