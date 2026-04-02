using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoinBot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTradingBotExecutionPilotFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ExchangeAccountId",
                table: "TradingBots",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "Leverage",
                table: "TradingBots",
                type: "decimal(18,8)",
                precision: 18,
                scale: 8,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MarginType",
                table: "TradingBots",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "Quantity",
                table: "TradingBots",
                type: "decimal(38,18)",
                precision: 38,
                scale: 18,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Symbol",
                table: "TradingBots",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_TradingBots_ExchangeAccountId",
                table: "TradingBots",
                column: "ExchangeAccountId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TradingBots_ExchangeAccountId",
                table: "TradingBots");

            migrationBuilder.DropColumn(
                name: "ExchangeAccountId",
                table: "TradingBots");

            migrationBuilder.DropColumn(
                name: "Leverage",
                table: "TradingBots");

            migrationBuilder.DropColumn(
                name: "MarginType",
                table: "TradingBots");

            migrationBuilder.DropColumn(
                name: "Quantity",
                table: "TradingBots");

            migrationBuilder.DropColumn(
                name: "Symbol",
                table: "TradingBots");
        }
    }
}
