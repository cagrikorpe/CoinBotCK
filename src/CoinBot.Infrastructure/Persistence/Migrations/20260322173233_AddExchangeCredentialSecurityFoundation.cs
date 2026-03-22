using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoinBot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddExchangeCredentialSecurityFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ApiKeyCiphertext",
                table: "ExchangeAccounts",
                type: "nvarchar(max)",
                maxLength: 4096,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ApiSecretCiphertext",
                table: "ExchangeAccounts",
                type: "nvarchar(max)",
                maxLength: 4096,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CredentialFingerprint",
                table: "ExchangeAccounts",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CredentialKeyVersion",
                table: "ExchangeAccounts",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CredentialLastAccessedAtUtc",
                table: "ExchangeAccounts",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CredentialLastRotatedAtUtc",
                table: "ExchangeAccounts",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CredentialRevalidateAfterUtc",
                table: "ExchangeAccounts",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CredentialRotateAfterUtc",
                table: "ExchangeAccounts",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CredentialStatus",
                table: "ExchangeAccounts",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Missing");

            migrationBuilder.AddColumn<DateTime>(
                name: "CredentialStoredAtUtc",
                table: "ExchangeAccounts",
                type: "datetime2",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExchangeAccounts_CredentialStatus_CredentialRevalidateAfterUtc",
                table: "ExchangeAccounts",
                columns: new[] { "CredentialStatus", "CredentialRevalidateAfterUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ExchangeAccounts_CredentialStatus_CredentialRotateAfterUtc",
                table: "ExchangeAccounts",
                columns: new[] { "CredentialStatus", "CredentialRotateAfterUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ExchangeAccounts_CredentialStatus_CredentialRevalidateAfterUtc",
                table: "ExchangeAccounts");

            migrationBuilder.DropIndex(
                name: "IX_ExchangeAccounts_CredentialStatus_CredentialRotateAfterUtc",
                table: "ExchangeAccounts");

            migrationBuilder.DropColumn(
                name: "ApiKeyCiphertext",
                table: "ExchangeAccounts");

            migrationBuilder.DropColumn(
                name: "ApiSecretCiphertext",
                table: "ExchangeAccounts");

            migrationBuilder.DropColumn(
                name: "CredentialFingerprint",
                table: "ExchangeAccounts");

            migrationBuilder.DropColumn(
                name: "CredentialKeyVersion",
                table: "ExchangeAccounts");

            migrationBuilder.DropColumn(
                name: "CredentialLastAccessedAtUtc",
                table: "ExchangeAccounts");

            migrationBuilder.DropColumn(
                name: "CredentialLastRotatedAtUtc",
                table: "ExchangeAccounts");

            migrationBuilder.DropColumn(
                name: "CredentialRevalidateAfterUtc",
                table: "ExchangeAccounts");

            migrationBuilder.DropColumn(
                name: "CredentialRotateAfterUtc",
                table: "ExchangeAccounts");

            migrationBuilder.DropColumn(
                name: "CredentialStatus",
                table: "ExchangeAccounts");

            migrationBuilder.DropColumn(
                name: "CredentialStoredAtUtc",
                table: "ExchangeAccounts");
        }
    }
}
