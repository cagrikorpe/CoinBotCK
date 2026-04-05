using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoinBot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSpotPrivatePlaneClosure : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ExchangePositions_ExchangeAccountId_Symbol_PositionSide",
                table: "ExchangePositions");

            migrationBuilder.DropIndex(
                name: "IX_ExchangeBalances_ExchangeAccountId_Asset",
                table: "ExchangeBalances");

            migrationBuilder.DropIndex(
                name: "IX_ExchangeAccountSyncStates_ExchangeAccountId",
                table: "ExchangeAccountSyncStates");

            migrationBuilder.AddColumn<string>(
                name: "Plane",
                table: "ExchangePositions",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<decimal>(
                name: "LockedBalance",
                table: "ExchangeBalances",
                type: "decimal(38,18)",
                precision: 38,
                scale: 18,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Plane",
                table: "ExchangeBalances",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Plane",
                table: "ExchangeAccountSyncStates",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_ExchangePositions_ExchangeAccountId_Plane_Symbol_PositionSide",
                table: "ExchangePositions",
                columns: new[] { "ExchangeAccountId", "Plane", "Symbol", "PositionSide" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExchangeBalances_ExchangeAccountId_Plane_Asset",
                table: "ExchangeBalances",
                columns: new[] { "ExchangeAccountId", "Plane", "Asset" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExchangeAccountSyncStates_ExchangeAccountId_Plane",
                table: "ExchangeAccountSyncStates",
                columns: new[] { "ExchangeAccountId", "Plane" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ExchangePositions_ExchangeAccountId_Plane_Symbol_PositionSide",
                table: "ExchangePositions");

            migrationBuilder.DropIndex(
                name: "IX_ExchangeBalances_ExchangeAccountId_Plane_Asset",
                table: "ExchangeBalances");

            migrationBuilder.DropIndex(
                name: "IX_ExchangeAccountSyncStates_ExchangeAccountId_Plane",
                table: "ExchangeAccountSyncStates");

            migrationBuilder.DropColumn(
                name: "Plane",
                table: "ExchangePositions");

            migrationBuilder.DropColumn(
                name: "LockedBalance",
                table: "ExchangeBalances");

            migrationBuilder.DropColumn(
                name: "Plane",
                table: "ExchangeBalances");

            migrationBuilder.DropColumn(
                name: "Plane",
                table: "ExchangeAccountSyncStates");

            migrationBuilder.CreateIndex(
                name: "IX_ExchangePositions_ExchangeAccountId_Symbol_PositionSide",
                table: "ExchangePositions",
                columns: new[] { "ExchangeAccountId", "Symbol", "PositionSide" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExchangeBalances_ExchangeAccountId_Asset",
                table: "ExchangeBalances",
                columns: new[] { "ExchangeAccountId", "Asset" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExchangeAccountSyncStates_ExchangeAccountId",
                table: "ExchangeAccountSyncStates",
                column: "ExchangeAccountId",
                unique: true);
        }
    }
}
