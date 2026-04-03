using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoinBot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDegradedModeMarketDataObservability : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "LatestContinuityGapCount",
                table: "DegradedModeStates",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LatestExpectedOpenTimeUtc",
                table: "DegradedModeStates",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LatestHeartbeatSource",
                table: "DegradedModeStates",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LatestSymbol",
                table: "DegradedModeStates",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LatestTimeframe",
                table: "DegradedModeStates",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LatestContinuityGapCount",
                table: "DegradedModeStates");

            migrationBuilder.DropColumn(
                name: "LatestExpectedOpenTimeUtc",
                table: "DegradedModeStates");

            migrationBuilder.DropColumn(
                name: "LatestHeartbeatSource",
                table: "DegradedModeStates");

            migrationBuilder.DropColumn(
                name: "LatestSymbol",
                table: "DegradedModeStates");

            migrationBuilder.DropColumn(
                name: "LatestTimeframe",
                table: "DegradedModeStates");
        }
    }
}
