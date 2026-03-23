using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoinBot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEncryptionAuditAndVirtualWalletSyncFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "AvailableValueInReferenceQuote",
                table: "DemoWallets",
                type: "decimal(38,18)",
                precision: 38,
                scale: 18,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "LastReferencePrice",
                table: "DemoWallets",
                type: "decimal(38,18)",
                precision: 38,
                scale: 18,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastValuationAtUtc",
                table: "DemoWallets",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastValuationSource",
                table: "DemoWallets",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReferenceQuoteAsset",
                table: "DemoWallets",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReferenceSymbol",
                table: "DemoWallets",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ReservedValueInReferenceQuote",
                table: "DemoWallets",
                type: "decimal(38,18)",
                precision: 38,
                scale: 18,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AvailableValueInReferenceQuote",
                table: "DemoWallets");

            migrationBuilder.DropColumn(
                name: "LastReferencePrice",
                table: "DemoWallets");

            migrationBuilder.DropColumn(
                name: "LastValuationAtUtc",
                table: "DemoWallets");

            migrationBuilder.DropColumn(
                name: "LastValuationSource",
                table: "DemoWallets");

            migrationBuilder.DropColumn(
                name: "ReferenceQuoteAsset",
                table: "DemoWallets");

            migrationBuilder.DropColumn(
                name: "ReferenceSymbol",
                table: "DemoWallets");

            migrationBuilder.DropColumn(
                name: "ReservedValueInReferenceQuote",
                table: "DemoWallets");
        }
    }
}
