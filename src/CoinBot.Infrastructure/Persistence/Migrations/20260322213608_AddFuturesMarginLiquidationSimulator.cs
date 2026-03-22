using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoinBot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFuturesMarginLiquidationSimulator : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "IsolatedMargin",
                table: "DemoPositions",
                type: "decimal(38,18)",
                precision: 38,
                scale: 18,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastFundingAppliedAtUtc",
                table: "DemoPositions",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "LastFundingRate",
                table: "DemoPositions",
                type: "decimal(18,8)",
                precision: 18,
                scale: 8,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "LastPrice",
                table: "DemoPositions",
                type: "decimal(38,18)",
                precision: 38,
                scale: 18,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "Leverage",
                table: "DemoPositions",
                type: "decimal(18,8)",
                precision: 18,
                scale: 8,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "LiquidationPrice",
                table: "DemoPositions",
                type: "decimal(38,18)",
                precision: 38,
                scale: 18,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "MaintenanceMargin",
                table: "DemoPositions",
                type: "decimal(38,18)",
                precision: 38,
                scale: 18,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "MaintenanceMarginRate",
                table: "DemoPositions",
                type: "decimal(18,8)",
                precision: 18,
                scale: 8,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "MarginBalance",
                table: "DemoPositions",
                type: "decimal(38,18)",
                precision: 38,
                scale: 18,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MarginMode",
                table: "DemoPositions",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "NetFundingInQuote",
                table: "DemoPositions",
                type: "decimal(38,18)",
                precision: 38,
                scale: 18,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "PositionKind",
                table: "DemoPositions",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Spot");

            migrationBuilder.AddColumn<decimal>(
                name: "FundingDeltaInQuote",
                table: "DemoLedgerTransactions",
                type: "decimal(38,18)",
                precision: 38,
                scale: 18,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "FundingRate",
                table: "DemoLedgerTransactions",
                type: "decimal(18,8)",
                precision: 18,
                scale: 8,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "LastPriceAfter",
                table: "DemoLedgerTransactions",
                type: "decimal(38,18)",
                precision: 38,
                scale: 18,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "Leverage",
                table: "DemoLedgerTransactions",
                type: "decimal(18,8)",
                precision: 18,
                scale: 8,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "LiquidationPriceAfter",
                table: "DemoLedgerTransactions",
                type: "decimal(38,18)",
                precision: 38,
                scale: 18,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "MaintenanceMarginAfter",
                table: "DemoLedgerTransactions",
                type: "decimal(38,18)",
                precision: 38,
                scale: 18,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "MaintenanceMarginRateAfter",
                table: "DemoLedgerTransactions",
                type: "decimal(18,8)",
                precision: 18,
                scale: 8,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "MarginBalanceAfter",
                table: "DemoLedgerTransactions",
                type: "decimal(38,18)",
                precision: 38,
                scale: 18,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MarginMode",
                table: "DemoLedgerTransactions",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "NetFundingInQuoteAfter",
                table: "DemoLedgerTransactions",
                type: "decimal(38,18)",
                precision: 38,
                scale: 18,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PositionKind",
                table: "DemoLedgerTransactions",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsolatedMargin",
                table: "DemoPositions");

            migrationBuilder.DropColumn(
                name: "LastFundingAppliedAtUtc",
                table: "DemoPositions");

            migrationBuilder.DropColumn(
                name: "LastFundingRate",
                table: "DemoPositions");

            migrationBuilder.DropColumn(
                name: "LastPrice",
                table: "DemoPositions");

            migrationBuilder.DropColumn(
                name: "Leverage",
                table: "DemoPositions");

            migrationBuilder.DropColumn(
                name: "LiquidationPrice",
                table: "DemoPositions");

            migrationBuilder.DropColumn(
                name: "MaintenanceMargin",
                table: "DemoPositions");

            migrationBuilder.DropColumn(
                name: "MaintenanceMarginRate",
                table: "DemoPositions");

            migrationBuilder.DropColumn(
                name: "MarginBalance",
                table: "DemoPositions");

            migrationBuilder.DropColumn(
                name: "MarginMode",
                table: "DemoPositions");

            migrationBuilder.DropColumn(
                name: "NetFundingInQuote",
                table: "DemoPositions");

            migrationBuilder.DropColumn(
                name: "PositionKind",
                table: "DemoPositions");

            migrationBuilder.DropColumn(
                name: "FundingDeltaInQuote",
                table: "DemoLedgerTransactions");

            migrationBuilder.DropColumn(
                name: "FundingRate",
                table: "DemoLedgerTransactions");

            migrationBuilder.DropColumn(
                name: "LastPriceAfter",
                table: "DemoLedgerTransactions");

            migrationBuilder.DropColumn(
                name: "Leverage",
                table: "DemoLedgerTransactions");

            migrationBuilder.DropColumn(
                name: "LiquidationPriceAfter",
                table: "DemoLedgerTransactions");

            migrationBuilder.DropColumn(
                name: "MaintenanceMarginAfter",
                table: "DemoLedgerTransactions");

            migrationBuilder.DropColumn(
                name: "MaintenanceMarginRateAfter",
                table: "DemoLedgerTransactions");

            migrationBuilder.DropColumn(
                name: "MarginBalanceAfter",
                table: "DemoLedgerTransactions");

            migrationBuilder.DropColumn(
                name: "MarginMode",
                table: "DemoLedgerTransactions");

            migrationBuilder.DropColumn(
                name: "NetFundingInQuoteAfter",
                table: "DemoLedgerTransactions");

            migrationBuilder.DropColumn(
                name: "PositionKind",
                table: "DemoLedgerTransactions");
        }
    }
}
