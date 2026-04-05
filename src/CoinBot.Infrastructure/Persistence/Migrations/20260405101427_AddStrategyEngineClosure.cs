using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoinBot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddStrategyEngineClosure : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TradingStrategyVersions_TradingStrategyId_Published",
                table: "TradingStrategyVersions");

            migrationBuilder.AddColumn<Guid>(
                name: "ActiveTradingStrategyVersionId",
                table: "TradingStrategies",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ActiveVersionActivatedAtUtc",
                table: "TradingStrategies",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "UsesExplicitVersionLifecycle",
                table: "TradingStrategies",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "TradingStrategyTemplates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TemplateKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    TemplateName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    Category = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    SchemaVersion = table.Column<int>(type: "int", nullable: false),
                    DefinitionJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    SourceTemplateKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    ArchivedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    OwnerUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TradingStrategyTemplates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TradingStrategyTemplates_AspNetUsers_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TradingStrategies_ActiveTradingStrategyVersionId",
                table: "TradingStrategies",
                column: "ActiveTradingStrategyVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_TradingStrategyTemplates_IsActive_CreatedDate",
                table: "TradingStrategyTemplates",
                columns: new[] { "IsActive", "CreatedDate" });

            migrationBuilder.CreateIndex(
                name: "IX_TradingStrategyTemplates_OwnerUserId",
                table: "TradingStrategyTemplates",
                column: "OwnerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_TradingStrategyTemplates_TemplateKey",
                table: "TradingStrategyTemplates",
                column: "TemplateKey",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_TradingStrategies_TradingStrategyVersions_ActiveTradingStrategyVersionId",
                table: "TradingStrategies",
                column: "ActiveTradingStrategyVersionId",
                principalTable: "TradingStrategyVersions",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_TradingStrategies_TradingStrategyVersions_ActiveTradingStrategyVersionId",
                table: "TradingStrategies");

            migrationBuilder.DropTable(
                name: "TradingStrategyTemplates");

            migrationBuilder.DropIndex(
                name: "IX_TradingStrategies_ActiveTradingStrategyVersionId",
                table: "TradingStrategies");

            migrationBuilder.DropColumn(
                name: "ActiveTradingStrategyVersionId",
                table: "TradingStrategies");

            migrationBuilder.DropColumn(
                name: "ActiveVersionActivatedAtUtc",
                table: "TradingStrategies");

            migrationBuilder.DropColumn(
                name: "UsesExplicitVersionLifecycle",
                table: "TradingStrategies");

            migrationBuilder.CreateIndex(
                name: "IX_TradingStrategyVersions_TradingStrategyId_Published",
                table: "TradingStrategyVersions",
                column: "TradingStrategyId",
                unique: true,
                filter: "[Status] = N'Published'");
        }
    }
}
