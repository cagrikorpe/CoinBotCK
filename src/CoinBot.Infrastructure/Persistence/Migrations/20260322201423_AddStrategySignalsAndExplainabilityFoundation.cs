using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoinBot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddStrategySignalsAndExplainabilityFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TradingStrategySignals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TradingStrategyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TradingStrategyVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StrategyVersionNumber = table.Column<int>(type: "int", nullable: false),
                    StrategySchemaVersion = table.Column<int>(type: "int", nullable: false),
                    SignalType = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    ExecutionEnvironment = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Symbol = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Timeframe = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    IndicatorOpenTimeUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IndicatorCloseTimeUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IndicatorReceivedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    GeneratedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ExplainabilitySchemaVersion = table.Column<int>(type: "int", nullable: false, defaultValue: 1),
                    IndicatorSnapshotJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    RuleResultSnapshotJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    OwnerUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TradingStrategySignals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TradingStrategySignals_AspNetUsers_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TradingStrategySignals_TradingStrategies_TradingStrategyId",
                        column: x => x.TradingStrategyId,
                        principalTable: "TradingStrategies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TradingStrategySignals_TradingStrategyVersions_TradingStrategyVersionId",
                        column: x => x.TradingStrategyVersionId,
                        principalTable: "TradingStrategyVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TradingStrategySignals_OwnerUserId",
                table: "TradingStrategySignals",
                column: "OwnerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_TradingStrategySignals_TradingStrategyId",
                table: "TradingStrategySignals",
                column: "TradingStrategyId");

            migrationBuilder.CreateIndex(
                name: "IX_TradingStrategySignals_TradingStrategyId_GeneratedAtUtc",
                table: "TradingStrategySignals",
                columns: new[] { "TradingStrategyId", "GeneratedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_TradingStrategySignals_TradingStrategyVersionId",
                table: "TradingStrategySignals",
                column: "TradingStrategyVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_TradingStrategySignals_TradingStrategyVersionId_SignalType_Symbol_Timeframe_IndicatorCloseTimeUtc",
                table: "TradingStrategySignals",
                columns: new[] { "TradingStrategyVersionId", "SignalType", "Symbol", "Timeframe", "IndicatorCloseTimeUtc" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TradingStrategySignals");
        }
    }
}
