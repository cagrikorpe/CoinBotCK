using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoinBot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTradingBotAllowedSymbolsScope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AllowedSymbolsCsv",
                table: "TradingBots",
                type: "nvarchar(512)",
                maxLength: 512,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AllowedSymbolsCsv",
                table: "TradingBots");
        }
    }
}
