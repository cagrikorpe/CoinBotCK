using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoinBot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddExecutionOrderLifecycleHardening : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "CooldownApplied",
                table: "ExecutionOrders",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "DuplicateSuppressed",
                table: "ExecutionOrders",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "ReduceOnly",
                table: "ExecutionOrders",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "RejectionStage",
                table: "ExecutionOrders",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "None");

            migrationBuilder.AddColumn<bool>(
                name: "RetryEligible",
                table: "ExecutionOrders",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "SubmittedToBroker",
                table: "ExecutionOrders",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.Sql("""
                UPDATE ExecutionOrders
                SET SubmittedToBroker = 1
                WHERE SubmittedAtUtc IS NOT NULL
                   OR ExternalOrderId IS NOT NULL
                   OR State IN ('Submitted', 'PartiallyFilled', 'CancelRequested', 'Filled', 'Cancelled');
                """);

            migrationBuilder.Sql("""
                UPDATE ExecutionOrders
                SET RejectionStage = CASE
                    WHEN State IN ('Rejected', 'Failed') AND SubmittedToBroker = 1 THEN 'PostSubmit'
                    WHEN State IN ('Rejected', 'Failed') THEN 'PreSubmit'
                    ELSE 'None'
                END;
                """);

            migrationBuilder.Sql("""
                UPDATE ExecutionOrders
                SET RetryEligible = CASE
                    WHEN State = 'Failed' AND SubmittedToBroker = 1 THEN 1
                    ELSE 0
                END,
                    CooldownApplied = CASE
                    WHEN SubmittedToBroker = 1 THEN 1
                    ELSE 0
                END;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CooldownApplied",
                table: "ExecutionOrders");

            migrationBuilder.DropColumn(
                name: "DuplicateSuppressed",
                table: "ExecutionOrders");

            migrationBuilder.DropColumn(
                name: "ReduceOnly",
                table: "ExecutionOrders");

            migrationBuilder.DropColumn(
                name: "RejectionStage",
                table: "ExecutionOrders");

            migrationBuilder.DropColumn(
                name: "RetryEligible",
                table: "ExecutionOrders");

            migrationBuilder.DropColumn(
                name: "SubmittedToBroker",
                table: "ExecutionOrders");
        }
    }
}
