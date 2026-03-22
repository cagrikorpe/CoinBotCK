using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoinBot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPrivateUserPlaneAndSyncFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ExchangeAccountSyncStates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ExchangeAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PrivateStreamConnectionState = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    LastListenKeyStartedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastListenKeyRenewedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastPrivateStreamEventAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastBalanceSyncedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastPositionSyncedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastStateReconciledAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DriftStatus = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    DriftSummary = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    LastDriftDetectedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ConsecutiveStreamFailureCount = table.Column<int>(type: "int", nullable: false),
                    LastErrorCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    OwnerUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExchangeAccountSyncStates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExchangeAccountSyncStates_AspNetUsers_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ExchangeAccountSyncStates_ExchangeAccounts_ExchangeAccountId",
                        column: x => x.ExchangeAccountId,
                        principalTable: "ExchangeAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ExchangeBalances",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ExchangeAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Asset = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    WalletBalance = table.Column<decimal>(type: "decimal(38,18)", precision: 38, scale: 18, nullable: false),
                    CrossWalletBalance = table.Column<decimal>(type: "decimal(38,18)", precision: 38, scale: 18, nullable: false),
                    AvailableBalance = table.Column<decimal>(type: "decimal(38,18)", precision: 38, scale: 18, nullable: true),
                    MaxWithdrawAmount = table.Column<decimal>(type: "decimal(38,18)", precision: 38, scale: 18, nullable: true),
                    ExchangeUpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    SyncedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    OwnerUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExchangeBalances", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExchangeBalances_AspNetUsers_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ExchangeBalances_ExchangeAccounts_ExchangeAccountId",
                        column: x => x.ExchangeAccountId,
                        principalTable: "ExchangeAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ExchangePositions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ExchangeAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Symbol = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    PositionSide = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Quantity = table.Column<decimal>(type: "decimal(38,18)", precision: 38, scale: 18, nullable: false),
                    EntryPrice = table.Column<decimal>(type: "decimal(38,18)", precision: 38, scale: 18, nullable: false),
                    BreakEvenPrice = table.Column<decimal>(type: "decimal(38,18)", precision: 38, scale: 18, nullable: false),
                    UnrealizedProfit = table.Column<decimal>(type: "decimal(38,18)", precision: 38, scale: 18, nullable: false),
                    MarginType = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    IsolatedWallet = table.Column<decimal>(type: "decimal(38,18)", precision: 38, scale: 18, nullable: false),
                    ExchangeUpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    SyncedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    OwnerUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExchangePositions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExchangePositions_AspNetUsers_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ExchangePositions_ExchangeAccounts_ExchangeAccountId",
                        column: x => x.ExchangeAccountId,
                        principalTable: "ExchangeAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ExchangeAccountSyncStates_ExchangeAccountId",
                table: "ExchangeAccountSyncStates",
                column: "ExchangeAccountId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExchangeAccountSyncStates_OwnerUserId",
                table: "ExchangeAccountSyncStates",
                column: "OwnerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ExchangeBalances_ExchangeAccountId_Asset",
                table: "ExchangeBalances",
                columns: new[] { "ExchangeAccountId", "Asset" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExchangeBalances_OwnerUserId",
                table: "ExchangeBalances",
                column: "OwnerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ExchangePositions_ExchangeAccountId_Symbol_PositionSide",
                table: "ExchangePositions",
                columns: new[] { "ExchangeAccountId", "Symbol", "PositionSide" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExchangePositions_OwnerUserId",
                table: "ExchangePositions",
                column: "OwnerUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ExchangeAccountSyncStates");

            migrationBuilder.DropTable(
                name: "ExchangeBalances");

            migrationBuilder.DropTable(
                name: "ExchangePositions");
        }
    }
}
