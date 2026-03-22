using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoinBot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddJobOrchestrationFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BackgroundJobLocks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    JobKey = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    JobType = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    WorkerInstanceId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    AcquiredAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastKeepAliveAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LeaseExpiresAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BackgroundJobLocks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BackgroundJobStates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    JobKey = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    JobType = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    BotId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    NextRunAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastStartedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastCompletedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastFailedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastHeartbeatAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ConsecutiveFailureCount = table.Column<int>(type: "int", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    LastErrorCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BackgroundJobStates", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BackgroundJobLocks_JobKey",
                table: "BackgroundJobLocks",
                column: "JobKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BackgroundJobLocks_JobType_LeaseExpiresAtUtc",
                table: "BackgroundJobLocks",
                columns: new[] { "JobType", "LeaseExpiresAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_BackgroundJobStates_BotId",
                table: "BackgroundJobStates",
                column: "BotId");

            migrationBuilder.CreateIndex(
                name: "IX_BackgroundJobStates_JobKey",
                table: "BackgroundJobStates",
                column: "JobKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BackgroundJobStates_JobType_Status_NextRunAtUtc",
                table: "BackgroundJobStates",
                columns: new[] { "JobType", "Status", "NextRunAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BackgroundJobLocks");

            migrationBuilder.DropTable(
                name: "BackgroundJobStates");
        }
    }
}
