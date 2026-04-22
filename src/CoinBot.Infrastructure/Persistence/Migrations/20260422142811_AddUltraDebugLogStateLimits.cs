using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoinBot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddUltraDebugLogStateLimits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "NormalLogsLimitMb",
                table: "UltraDebugLogStates",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "UltraLogsLimitMb",
                table: "UltraDebugLogStates",
                type: "int",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "NormalLogsLimitMb",
                table: "UltraDebugLogStates");

            migrationBuilder.DropColumn(
                name: "UltraLogsLimitMb",
                table: "UltraDebugLogStates");
        }
    }
}
