using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoinBot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDecisionTraceExecutionDecisionSurface : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ContinuityGapCount",
                table: "DecisionTraces",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ContinuityGapLastSeenAtUtc",
                table: "DecisionTraces",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ContinuityGapStartedAtUtc",
                table: "DecisionTraces",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ContinuityRecoveredAtUtc",
                table: "DecisionTraces",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ContinuityState",
                table: "DecisionTraces",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DataAgeMs",
                table: "DecisionTraces",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DecisionAtUtc",
                table: "DecisionTraces",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DecisionReasonCode",
                table: "DecisionTraces",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DecisionReasonType",
                table: "DecisionTraces",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DecisionSummary",
                table: "DecisionTraces",
                type: "nvarchar(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastCandleAtUtc",
                table: "DecisionTraces",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StaleReason",
                table: "DecisionTraces",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "StaleThresholdMs",
                table: "DecisionTraces",
                type: "int",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_DecisionTraces_DecisionReasonCode_CreatedAtUtc",
                table: "DecisionTraces",
                columns: new[] { "DecisionReasonCode", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_DecisionTraces_DecisionReasonType_CreatedAtUtc",
                table: "DecisionTraces",
                columns: new[] { "DecisionReasonType", "CreatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_DecisionTraces_DecisionReasonCode_CreatedAtUtc",
                table: "DecisionTraces");

            migrationBuilder.DropIndex(
                name: "IX_DecisionTraces_DecisionReasonType_CreatedAtUtc",
                table: "DecisionTraces");

            migrationBuilder.DropColumn(
                name: "ContinuityGapCount",
                table: "DecisionTraces");

            migrationBuilder.DropColumn(
                name: "ContinuityGapLastSeenAtUtc",
                table: "DecisionTraces");

            migrationBuilder.DropColumn(
                name: "ContinuityGapStartedAtUtc",
                table: "DecisionTraces");

            migrationBuilder.DropColumn(
                name: "ContinuityRecoveredAtUtc",
                table: "DecisionTraces");

            migrationBuilder.DropColumn(
                name: "ContinuityState",
                table: "DecisionTraces");

            migrationBuilder.DropColumn(
                name: "DataAgeMs",
                table: "DecisionTraces");

            migrationBuilder.DropColumn(
                name: "DecisionAtUtc",
                table: "DecisionTraces");

            migrationBuilder.DropColumn(
                name: "DecisionReasonCode",
                table: "DecisionTraces");

            migrationBuilder.DropColumn(
                name: "DecisionReasonType",
                table: "DecisionTraces");

            migrationBuilder.DropColumn(
                name: "DecisionSummary",
                table: "DecisionTraces");

            migrationBuilder.DropColumn(
                name: "LastCandleAtUtc",
                table: "DecisionTraces");

            migrationBuilder.DropColumn(
                name: "StaleReason",
                table: "DecisionTraces");

            migrationBuilder.DropColumn(
                name: "StaleThresholdMs",
                table: "DecisionTraces");
        }
    }
}
