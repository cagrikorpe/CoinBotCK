using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoinBot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRiskEngineLimitsAndHandoffSnapshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CoinSpecificExposureLimitsJson",
                table: "RiskProfiles",
                type: "nvarchar(1024)",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MaxConcurrentPositions",
                table: "RiskProfiles",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "MaxSymbolExposurePercentage",
                table: "RiskProfiles",
                type: "decimal(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "MaxWeeklyLossPercentage",
                table: "RiskProfiles",
                type: "decimal(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RiskBaseAsset",
                table: "MarketScannerHandoffAttempts",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "RiskCurrentCoinExposurePercentage",
                table: "MarketScannerHandoffAttempts",
                type: "decimal(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "RiskCurrentDailyLossPercentage",
                table: "MarketScannerHandoffAttempts",
                type: "decimal(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "RiskCurrentLeverage",
                table: "MarketScannerHandoffAttempts",
                type: "decimal(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RiskCurrentOpenPositions",
                table: "MarketScannerHandoffAttempts",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "RiskCurrentSymbolExposurePercentage",
                table: "MarketScannerHandoffAttempts",
                type: "decimal(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "RiskCurrentWeeklyLossPercentage",
                table: "MarketScannerHandoffAttempts",
                type: "decimal(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "RiskMaxCoinExposurePercentage",
                table: "MarketScannerHandoffAttempts",
                type: "decimal(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RiskMaxConcurrentPositions",
                table: "MarketScannerHandoffAttempts",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "RiskMaxDailyLossPercentage",
                table: "MarketScannerHandoffAttempts",
                type: "decimal(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "RiskMaxLeverage",
                table: "MarketScannerHandoffAttempts",
                type: "decimal(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "RiskMaxSymbolExposurePercentage",
                table: "MarketScannerHandoffAttempts",
                type: "decimal(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "RiskMaxWeeklyLossPercentage",
                table: "MarketScannerHandoffAttempts",
                type: "decimal(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RiskOutcome",
                table: "MarketScannerHandoffAttempts",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "RiskProjectedCoinExposurePercentage",
                table: "MarketScannerHandoffAttempts",
                type: "decimal(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "RiskProjectedLeverage",
                table: "MarketScannerHandoffAttempts",
                type: "decimal(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RiskProjectedOpenPositions",
                table: "MarketScannerHandoffAttempts",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "RiskProjectedSymbolExposurePercentage",
                table: "MarketScannerHandoffAttempts",
                type: "decimal(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RiskSummary",
                table: "MarketScannerHandoffAttempts",
                type: "nvarchar(1024)",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RiskVetoReasonCode",
                table: "MarketScannerHandoffAttempts",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CoinSpecificExposureLimitsJson",
                table: "RiskProfiles");

            migrationBuilder.DropColumn(
                name: "MaxConcurrentPositions",
                table: "RiskProfiles");

            migrationBuilder.DropColumn(
                name: "MaxSymbolExposurePercentage",
                table: "RiskProfiles");

            migrationBuilder.DropColumn(
                name: "MaxWeeklyLossPercentage",
                table: "RiskProfiles");

            migrationBuilder.DropColumn(
                name: "RiskBaseAsset",
                table: "MarketScannerHandoffAttempts");

            migrationBuilder.DropColumn(
                name: "RiskCurrentCoinExposurePercentage",
                table: "MarketScannerHandoffAttempts");

            migrationBuilder.DropColumn(
                name: "RiskCurrentDailyLossPercentage",
                table: "MarketScannerHandoffAttempts");

            migrationBuilder.DropColumn(
                name: "RiskCurrentLeverage",
                table: "MarketScannerHandoffAttempts");

            migrationBuilder.DropColumn(
                name: "RiskCurrentOpenPositions",
                table: "MarketScannerHandoffAttempts");

            migrationBuilder.DropColumn(
                name: "RiskCurrentSymbolExposurePercentage",
                table: "MarketScannerHandoffAttempts");

            migrationBuilder.DropColumn(
                name: "RiskCurrentWeeklyLossPercentage",
                table: "MarketScannerHandoffAttempts");

            migrationBuilder.DropColumn(
                name: "RiskMaxCoinExposurePercentage",
                table: "MarketScannerHandoffAttempts");

            migrationBuilder.DropColumn(
                name: "RiskMaxConcurrentPositions",
                table: "MarketScannerHandoffAttempts");

            migrationBuilder.DropColumn(
                name: "RiskMaxDailyLossPercentage",
                table: "MarketScannerHandoffAttempts");

            migrationBuilder.DropColumn(
                name: "RiskMaxLeverage",
                table: "MarketScannerHandoffAttempts");

            migrationBuilder.DropColumn(
                name: "RiskMaxSymbolExposurePercentage",
                table: "MarketScannerHandoffAttempts");

            migrationBuilder.DropColumn(
                name: "RiskMaxWeeklyLossPercentage",
                table: "MarketScannerHandoffAttempts");

            migrationBuilder.DropColumn(
                name: "RiskOutcome",
                table: "MarketScannerHandoffAttempts");

            migrationBuilder.DropColumn(
                name: "RiskProjectedCoinExposurePercentage",
                table: "MarketScannerHandoffAttempts");

            migrationBuilder.DropColumn(
                name: "RiskProjectedLeverage",
                table: "MarketScannerHandoffAttempts");

            migrationBuilder.DropColumn(
                name: "RiskProjectedOpenPositions",
                table: "MarketScannerHandoffAttempts");

            migrationBuilder.DropColumn(
                name: "RiskProjectedSymbolExposurePercentage",
                table: "MarketScannerHandoffAttempts");

            migrationBuilder.DropColumn(
                name: "RiskSummary",
                table: "MarketScannerHandoffAttempts");

            migrationBuilder.DropColumn(
                name: "RiskVetoReasonCode",
                table: "MarketScannerHandoffAttempts");
        }
    }
}
