using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoinBot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSpotExecutionPlaneClosure : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ExecutionOrders_ExecutorKind_State_LastReconciledAtUtc",
                table: "ExecutionOrders");

            migrationBuilder.AddColumn<string>(
                name: "Plane",
                table: "ExecutionOrders",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Futures");

            migrationBuilder.CreateIndex(
                name: "IX_ExecutionOrders_ExecutorKind_Plane_State_LastReconciledAtUtc",
                table: "ExecutionOrders",
                columns: new[] { "ExecutorKind", "Plane", "State", "LastReconciledAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ExecutionOrders_ExecutorKind_Plane_State_LastReconciledAtUtc",
                table: "ExecutionOrders");

            migrationBuilder.DropColumn(
                name: "Plane",
                table: "ExecutionOrders");

            migrationBuilder.CreateIndex(
                name: "IX_ExecutionOrders_ExecutorKind_State_LastReconciledAtUtc",
                table: "ExecutionOrders",
                columns: new[] { "ExecutorKind", "State", "LastReconciledAtUtc" });
        }
    }
}
