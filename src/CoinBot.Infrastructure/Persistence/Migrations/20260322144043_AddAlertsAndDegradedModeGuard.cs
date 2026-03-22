using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoinBot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAlertsAndDegradedModeGuard : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DegradedModeStates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StateCode = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ReasonCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    SignalFlowBlocked = table.Column<bool>(type: "bit", nullable: false),
                    ExecutionFlowBlocked = table.Column<bool>(type: "bit", nullable: false),
                    LatestDataTimestampAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LatestHeartbeatReceivedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LatestClockDriftMilliseconds = table.Column<int>(type: "int", nullable: true),
                    LastStateChangedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DegradedModeStates", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DegradedModeStates");
        }
    }
}
