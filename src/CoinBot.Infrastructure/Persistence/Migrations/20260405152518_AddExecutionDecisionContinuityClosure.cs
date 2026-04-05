using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoinBot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddExecutionDecisionContinuityClosure : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LatestContinuityGapLastSeenAtUtc",
                table: "DegradedModeStates",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LatestContinuityGapStartedAtUtc",
                table: "DegradedModeStates",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LatestContinuityRecoveredAtUtc",
                table: "DegradedModeStates",
                type: "datetime2",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LatestContinuityGapLastSeenAtUtc",
                table: "DegradedModeStates");

            migrationBuilder.DropColumn(
                name: "LatestContinuityGapStartedAtUtc",
                table: "DegradedModeStates");

            migrationBuilder.DropColumn(
                name: "LatestContinuityRecoveredAtUtc",
                table: "DegradedModeStates");
        }
    }
}
