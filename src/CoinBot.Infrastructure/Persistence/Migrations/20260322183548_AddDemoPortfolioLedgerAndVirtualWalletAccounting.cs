using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoinBot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDemoPortfolioLedgerAndVirtualWalletAccounting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DemoLedgerTransactions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OperationId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    TransactionType = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    BotId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PositionScopeKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    OrderId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    FillId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    Symbol = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    BaseAsset = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    QuoteAsset = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    Side = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    Quantity = table.Column<decimal>(type: "decimal(38,18)", precision: 38, scale: 18, nullable: true),
                    Price = table.Column<decimal>(type: "decimal(38,18)", precision: 38, scale: 18, nullable: true),
                    FeeAsset = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    FeeAmount = table.Column<decimal>(type: "decimal(38,18)", precision: 38, scale: 18, nullable: true),
                    FeeAmountInQuote = table.Column<decimal>(type: "decimal(38,18)", precision: 38, scale: 18, nullable: true),
                    RealizedPnlDelta = table.Column<decimal>(type: "decimal(38,18)", precision: 38, scale: 18, nullable: true),
                    PositionQuantityAfter = table.Column<decimal>(type: "decimal(38,18)", precision: 38, scale: 18, nullable: true),
                    PositionCostBasisAfter = table.Column<decimal>(type: "decimal(38,18)", precision: 38, scale: 18, nullable: true),
                    PositionAverageEntryPriceAfter = table.Column<decimal>(type: "decimal(38,18)", precision: 38, scale: 18, nullable: true),
                    CumulativeRealizedPnlAfter = table.Column<decimal>(type: "decimal(38,18)", precision: 38, scale: 18, nullable: true),
                    UnrealizedPnlAfter = table.Column<decimal>(type: "decimal(38,18)", precision: 38, scale: 18, nullable: true),
                    CumulativeFeesInQuoteAfter = table.Column<decimal>(type: "decimal(38,18)", precision: 38, scale: 18, nullable: true),
                    MarkPriceAfter = table.Column<decimal>(type: "decimal(38,18)", precision: 38, scale: 18, nullable: true),
                    OccurredAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    OwnerUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DemoLedgerTransactions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DemoLedgerTransactions_AspNetUsers_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DemoPositions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BotId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PositionScopeKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Symbol = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    BaseAsset = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    QuoteAsset = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Quantity = table.Column<decimal>(type: "decimal(38,18)", precision: 38, scale: 18, nullable: false),
                    CostBasis = table.Column<decimal>(type: "decimal(38,18)", precision: 38, scale: 18, nullable: false),
                    AverageEntryPrice = table.Column<decimal>(type: "decimal(38,18)", precision: 38, scale: 18, nullable: false),
                    RealizedPnl = table.Column<decimal>(type: "decimal(38,18)", precision: 38, scale: 18, nullable: false),
                    UnrealizedPnl = table.Column<decimal>(type: "decimal(38,18)", precision: 38, scale: 18, nullable: false),
                    TotalFeesInQuote = table.Column<decimal>(type: "decimal(38,18)", precision: 38, scale: 18, nullable: false),
                    LastMarkPrice = table.Column<decimal>(type: "decimal(38,18)", precision: 38, scale: 18, nullable: true),
                    LastFillPrice = table.Column<decimal>(type: "decimal(38,18)", precision: 38, scale: 18, nullable: true),
                    LastFilledAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastValuationAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    OwnerUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DemoPositions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DemoPositions_AspNetUsers_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DemoWallets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Asset = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    AvailableBalance = table.Column<decimal>(type: "decimal(38,18)", precision: 38, scale: 18, nullable: false),
                    ReservedBalance = table.Column<decimal>(type: "decimal(38,18)", precision: 38, scale: 18, nullable: false),
                    LastActivityAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    OwnerUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DemoWallets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DemoWallets_AspNetUsers_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DemoLedgerEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DemoLedgerTransactionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Asset = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    AvailableDelta = table.Column<decimal>(type: "decimal(38,18)", precision: 38, scale: 18, nullable: false),
                    ReservedDelta = table.Column<decimal>(type: "decimal(38,18)", precision: 38, scale: 18, nullable: false),
                    AvailableBalanceAfter = table.Column<decimal>(type: "decimal(38,18)", precision: 38, scale: 18, nullable: false),
                    ReservedBalanceAfter = table.Column<decimal>(type: "decimal(38,18)", precision: 38, scale: 18, nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    OwnerUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DemoLedgerEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DemoLedgerEntries_AspNetUsers_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DemoLedgerEntries_DemoLedgerTransactions_DemoLedgerTransactionId",
                        column: x => x.DemoLedgerTransactionId,
                        principalTable: "DemoLedgerTransactions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DemoLedgerEntries_DemoLedgerTransactionId",
                table: "DemoLedgerEntries",
                column: "DemoLedgerTransactionId");

            migrationBuilder.CreateIndex(
                name: "IX_DemoLedgerEntries_OwnerUserId",
                table: "DemoLedgerEntries",
                column: "OwnerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_DemoLedgerTransactions_OwnerUserId",
                table: "DemoLedgerTransactions",
                column: "OwnerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_DemoLedgerTransactions_OwnerUserId_OccurredAtUtc",
                table: "DemoLedgerTransactions",
                columns: new[] { "OwnerUserId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_DemoLedgerTransactions_OwnerUserId_OperationId",
                table: "DemoLedgerTransactions",
                columns: new[] { "OwnerUserId", "OperationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DemoPositions_BotId",
                table: "DemoPositions",
                column: "BotId");

            migrationBuilder.CreateIndex(
                name: "IX_DemoPositions_OwnerUserId",
                table: "DemoPositions",
                column: "OwnerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_DemoPositions_OwnerUserId_PositionScopeKey_Symbol",
                table: "DemoPositions",
                columns: new[] { "OwnerUserId", "PositionScopeKey", "Symbol" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DemoWallets_OwnerUserId",
                table: "DemoWallets",
                column: "OwnerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_DemoWallets_OwnerUserId_Asset",
                table: "DemoWallets",
                columns: new[] { "OwnerUserId", "Asset" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DemoLedgerEntries");

            migrationBuilder.DropTable(
                name: "DemoPositions");

            migrationBuilder.DropTable(
                name: "DemoWallets");

            migrationBuilder.DropTable(
                name: "DemoLedgerTransactions");
        }
    }
}
