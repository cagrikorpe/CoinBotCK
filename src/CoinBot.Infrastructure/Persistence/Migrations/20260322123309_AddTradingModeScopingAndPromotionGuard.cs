using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoinBot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTradingModeScopingAndPromotionGuard : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "OpenOrderCount",
                table: "TradingBots",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "OpenPositionCount",
                table: "TradingBots",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "TradingModeApprovalReference",
                table: "TradingBots",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "TradingModeApprovedAtUtc",
                table: "TradingBots",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TradingModeOverride",
                table: "TradingBots",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LiveModeApprovalReference",
                table: "GlobalExecutionSwitches",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LiveModeApprovedAtUtc",
                table: "GlobalExecutionSwitches",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TradingModeApprovalReference",
                table: "AspNetUsers",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "TradingModeApprovedAtUtc",
                table: "AspNetUsers",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TradingModeOverride",
                table: "AspNetUsers",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "TradingStrategies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StrategyKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    PromotionState = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    PublishedMode = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    PublishedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LivePromotionApprovedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LivePromotionApprovalReference = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    OwnerUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TradingStrategies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TradingStrategies_AspNetUsers_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TradingStrategies_OwnerUserId",
                table: "TradingStrategies",
                column: "OwnerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_TradingStrategies_OwnerUserId_StrategyKey",
                table: "TradingStrategies",
                columns: new[] { "OwnerUserId", "StrategyKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TradingStrategies");

            migrationBuilder.DropColumn(
                name: "OpenOrderCount",
                table: "TradingBots");

            migrationBuilder.DropColumn(
                name: "OpenPositionCount",
                table: "TradingBots");

            migrationBuilder.DropColumn(
                name: "TradingModeApprovalReference",
                table: "TradingBots");

            migrationBuilder.DropColumn(
                name: "TradingModeApprovedAtUtc",
                table: "TradingBots");

            migrationBuilder.DropColumn(
                name: "TradingModeOverride",
                table: "TradingBots");

            migrationBuilder.DropColumn(
                name: "LiveModeApprovalReference",
                table: "GlobalExecutionSwitches");

            migrationBuilder.DropColumn(
                name: "LiveModeApprovedAtUtc",
                table: "GlobalExecutionSwitches");

            migrationBuilder.DropColumn(
                name: "TradingModeApprovalReference",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "TradingModeApprovedAtUtc",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "TradingModeOverride",
                table: "AspNetUsers");
        }
    }
}
