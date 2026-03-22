using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoinBot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddStrategyEngineCoreFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TradingStrategyVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TradingStrategyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SchemaVersion = table.Column<int>(type: "int", nullable: false),
                    VersionNumber = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    DefinitionJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    PublishedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ArchivedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    OwnerUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TradingStrategyVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TradingStrategyVersions_AspNetUsers_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TradingStrategyVersions_TradingStrategies_TradingStrategyId",
                        column: x => x.TradingStrategyId,
                        principalTable: "TradingStrategies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TradingStrategyVersions_OwnerUserId",
                table: "TradingStrategyVersions",
                column: "OwnerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_TradingStrategyVersions_TradingStrategyId_Draft",
                table: "TradingStrategyVersions",
                column: "TradingStrategyId",
                unique: true,
                filter: "[Status] = N'Draft'");

            migrationBuilder.CreateIndex(
                name: "IX_TradingStrategyVersions_TradingStrategyId_Published",
                table: "TradingStrategyVersions",
                column: "TradingStrategyId",
                unique: true,
                filter: "[Status] = N'Published'");

            migrationBuilder.CreateIndex(
                name: "IX_TradingStrategyVersions_TradingStrategyId_Status",
                table: "TradingStrategyVersions",
                columns: new[] { "TradingStrategyId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_TradingStrategyVersions_TradingStrategyId_VersionNumber",
                table: "TradingStrategyVersions",
                columns: new[] { "TradingStrategyId", "VersionNumber" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TradingStrategyVersions");
        }
    }
}
