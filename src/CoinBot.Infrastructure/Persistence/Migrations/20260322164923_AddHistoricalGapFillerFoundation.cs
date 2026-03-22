using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoinBot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddHistoricalGapFillerFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "HistoricalMarketCandles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Symbol = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Interval = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    OpenTimeUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CloseTimeUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    OpenPrice = table.Column<decimal>(type: "decimal(38,18)", precision: 38, scale: 18, nullable: false),
                    HighPrice = table.Column<decimal>(type: "decimal(38,18)", precision: 38, scale: 18, nullable: false),
                    LowPrice = table.Column<decimal>(type: "decimal(38,18)", precision: 38, scale: 18, nullable: false),
                    ClosePrice = table.Column<decimal>(type: "decimal(38,18)", precision: 38, scale: 18, nullable: false),
                    Volume = table.Column<decimal>(type: "decimal(38,18)", precision: 38, scale: 18, nullable: false),
                    ReceivedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Source = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HistoricalMarketCandles", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_HistoricalMarketCandles_Symbol_Interval_CloseTimeUtc",
                table: "HistoricalMarketCandles",
                columns: new[] { "Symbol", "Interval", "CloseTimeUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_HistoricalMarketCandles_Symbol_Interval_OpenTimeUtc",
                table: "HistoricalMarketCandles",
                columns: new[] { "Symbol", "Interval", "OpenTimeUtc" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HistoricalMarketCandles");
        }
    }
}
