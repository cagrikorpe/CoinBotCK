using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoinBot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddStrategyEngineHardeningClosure : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ActiveTradingStrategyTemplateRevisionId",
                table: "TradingStrategyTemplates",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "LatestTradingStrategyTemplateRevisionId",
                table: "TradingStrategyTemplates",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "ActivationConcurrencyToken",
                table: "TradingStrategies",
                type: "rowversion",
                rowVersion: true,
                nullable: false,
                defaultValue: new byte[0]);

            migrationBuilder.CreateTable(
                name: "TradingStrategyTemplateRevisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TradingStrategyTemplateId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RevisionNumber = table.Column<int>(type: "int", nullable: false),
                    SchemaVersion = table.Column<int>(type: "int", nullable: false),
                    DefinitionJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ValidationStatusCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ValidationSummary = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: false),
                    SourceTemplateKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    SourceRevisionNumber = table.Column<int>(type: "int", nullable: true),
                    ArchivedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    OwnerUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TradingStrategyTemplateRevisions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TradingStrategyTemplateRevisions_AspNetUsers_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TradingStrategyTemplateRevisions_TradingStrategyTemplates_TradingStrategyTemplateId",
                        column: x => x.TradingStrategyTemplateId,
                        principalTable: "TradingStrategyTemplates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TradingStrategyTemplates_ActiveTradingStrategyTemplateRevisionId",
                table: "TradingStrategyTemplates",
                column: "ActiveTradingStrategyTemplateRevisionId");

            migrationBuilder.CreateIndex(
                name: "IX_TradingStrategyTemplates_LatestTradingStrategyTemplateRevisionId",
                table: "TradingStrategyTemplates",
                column: "LatestTradingStrategyTemplateRevisionId");

            migrationBuilder.CreateIndex(
                name: "IX_TradingStrategyTemplateRevisions_OwnerUserId",
                table: "TradingStrategyTemplateRevisions",
                column: "OwnerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_TradingStrategyTemplateRevisions_SourceTemplateKey_SourceRevisionNumber",
                table: "TradingStrategyTemplateRevisions",
                columns: new[] { "SourceTemplateKey", "SourceRevisionNumber" });

            migrationBuilder.CreateIndex(
                name: "IX_TradingStrategyTemplateRevisions_TradingStrategyTemplateId",
                table: "TradingStrategyTemplateRevisions",
                column: "TradingStrategyTemplateId");

            migrationBuilder.CreateIndex(
                name: "IX_TradingStrategyTemplateRevisions_TradingStrategyTemplateId_RevisionNumber",
                table: "TradingStrategyTemplateRevisions",
                columns: new[] { "TradingStrategyTemplateId", "RevisionNumber" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_TradingStrategyTemplates_TradingStrategyTemplateRevisions_ActiveTradingStrategyTemplateRevisionId",
                table: "TradingStrategyTemplates",
                column: "ActiveTradingStrategyTemplateRevisionId",
                principalTable: "TradingStrategyTemplateRevisions",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_TradingStrategyTemplates_TradingStrategyTemplateRevisions_LatestTradingStrategyTemplateRevisionId",
                table: "TradingStrategyTemplates",
                column: "LatestTradingStrategyTemplateRevisionId",
                principalTable: "TradingStrategyTemplateRevisions",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_TradingStrategyTemplates_TradingStrategyTemplateRevisions_ActiveTradingStrategyTemplateRevisionId",
                table: "TradingStrategyTemplates");

            migrationBuilder.DropForeignKey(
                name: "FK_TradingStrategyTemplates_TradingStrategyTemplateRevisions_LatestTradingStrategyTemplateRevisionId",
                table: "TradingStrategyTemplates");

            migrationBuilder.DropTable(
                name: "TradingStrategyTemplateRevisions");

            migrationBuilder.DropIndex(
                name: "IX_TradingStrategyTemplates_ActiveTradingStrategyTemplateRevisionId",
                table: "TradingStrategyTemplates");

            migrationBuilder.DropIndex(
                name: "IX_TradingStrategyTemplates_LatestTradingStrategyTemplateRevisionId",
                table: "TradingStrategyTemplates");

            migrationBuilder.DropColumn(
                name: "ActiveTradingStrategyTemplateRevisionId",
                table: "TradingStrategyTemplates");

            migrationBuilder.DropColumn(
                name: "LatestTradingStrategyTemplateRevisionId",
                table: "TradingStrategyTemplates");

            migrationBuilder.DropColumn(
                name: "ActivationConcurrencyToken",
                table: "TradingStrategies");
        }
    }
}
