using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoinBot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSpotPortfolioAuditParityClosure : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SpotPortfolioFills",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ExchangeAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ExecutionOrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Plane = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false, defaultValue: "Spot"),
                    Symbol = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    BaseAsset = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    QuoteAsset = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Side = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    ExchangeOrderId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ClientOrderId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    TradeId = table.Column<long>(type: "bigint", nullable: false),
                    Quantity = table.Column<decimal>(type: "decimal(38,18)", precision: 38, scale: 18, nullable: false),
                    QuoteQuantity = table.Column<decimal>(type: "decimal(38,18)", precision: 38, scale: 18, nullable: false),
                    Price = table.Column<decimal>(type: "decimal(38,18)", precision: 38, scale: 18, nullable: false),
                    FeeAsset = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    FeeAmount = table.Column<decimal>(type: "decimal(38,18)", precision: 38, scale: 18, nullable: true),
                    FeeAmountInQuote = table.Column<decimal>(type: "decimal(38,18)", precision: 38, scale: 18, nullable: false),
                    RealizedPnlDelta = table.Column<decimal>(type: "decimal(38,18)", precision: 38, scale: 18, nullable: false),
                    HoldingQuantityAfter = table.Column<decimal>(type: "decimal(38,18)", precision: 38, scale: 18, nullable: false),
                    HoldingCostBasisAfter = table.Column<decimal>(type: "decimal(38,18)", precision: 38, scale: 18, nullable: false),
                    HoldingAverageCostAfter = table.Column<decimal>(type: "decimal(38,18)", precision: 38, scale: 18, nullable: false),
                    CumulativeRealizedPnlAfter = table.Column<decimal>(type: "decimal(38,18)", precision: 38, scale: 18, nullable: false),
                    CumulativeFeesInQuoteAfter = table.Column<decimal>(type: "decimal(38,18)", precision: 38, scale: 18, nullable: false),
                    Source = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    RootCorrelationId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    OccurredAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    OwnerUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SpotPortfolioFills", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SpotPortfolioFills_AspNetUsers_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SpotPortfolioFills_ExecutionOrders_ExecutionOrderId",
                        column: x => x.ExecutionOrderId,
                        principalTable: "ExecutionOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SpotPortfolioFills_ExchangeAccountId_Symbol_OccurredAtUtc",
                table: "SpotPortfolioFills",
                columns: new[] { "ExchangeAccountId", "Symbol", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_SpotPortfolioFills_ExchangeAccountId_Symbol_TradeId",
                table: "SpotPortfolioFills",
                columns: new[] { "ExchangeAccountId", "Symbol", "TradeId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SpotPortfolioFills_ExecutionOrderId",
                table: "SpotPortfolioFills",
                column: "ExecutionOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_SpotPortfolioFills_OwnerUserId",
                table: "SpotPortfolioFills",
                column: "OwnerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_SpotPortfolioFills_RootCorrelationId",
                table: "SpotPortfolioFills",
                column: "RootCorrelationId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SpotPortfolioFills");
        }
    }
}
