using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoinBot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTradingFeatureSnapshotKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CorrelationId",
                table: "TradingFeatureSnapshots",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "FeatureAnchorTimeUtc",
                table: "TradingFeatureSnapshots",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SnapshotKey",
                table: "TradingFeatureSnapshots",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_TradingFeatureSnapshots_CorrelationId",
                table: "TradingFeatureSnapshots",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_TradingFeatureSnapshots_OwnerUserId_BotId_Symbol_Timeframe_FeatureAnchorTimeUtc",
                table: "TradingFeatureSnapshots",
                columns: new[] { "OwnerUserId", "BotId", "Symbol", "Timeframe", "FeatureAnchorTimeUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_TradingFeatureSnapshots_OwnerUserId_SnapshotKey",
                table: "TradingFeatureSnapshots",
                columns: new[] { "OwnerUserId", "SnapshotKey" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TradingFeatureSnapshots_CorrelationId",
                table: "TradingFeatureSnapshots");

            migrationBuilder.DropIndex(
                name: "IX_TradingFeatureSnapshots_OwnerUserId_BotId_Symbol_Timeframe_FeatureAnchorTimeUtc",
                table: "TradingFeatureSnapshots");

            migrationBuilder.DropIndex(
                name: "IX_TradingFeatureSnapshots_OwnerUserId_SnapshotKey",
                table: "TradingFeatureSnapshots");

            migrationBuilder.DropColumn(
                name: "CorrelationId",
                table: "TradingFeatureSnapshots");

            migrationBuilder.DropColumn(
                name: "FeatureAnchorTimeUtc",
                table: "TradingFeatureSnapshots");

            migrationBuilder.DropColumn(
                name: "SnapshotKey",
                table: "TradingFeatureSnapshots");
        }
    }
}
