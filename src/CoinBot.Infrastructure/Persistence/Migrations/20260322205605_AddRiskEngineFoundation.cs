using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoinBot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRiskEngineFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RiskEvaluationJson",
                table: "TradingStrategySignals",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "MaxLeverage",
                table: "RiskProfiles",
                type: "decimal(18,4)",
                precision: 18,
                scale: 4,
                nullable: false,
                defaultValue: 1m);

            migrationBuilder.CreateTable(
                name: "TradingStrategySignalVetoes",
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
                    EvaluatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ReasonCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    RiskEvaluationJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    OwnerUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TradingStrategySignalVetoes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TradingStrategySignalVetoes_AspNetUsers_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TradingStrategySignalVetoes_TradingStrategies_TradingStrategyId",
                        column: x => x.TradingStrategyId,
                        principalTable: "TradingStrategies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TradingStrategySignalVetoes_TradingStrategyVersions_TradingStrategyVersionId",
                        column: x => x.TradingStrategyVersionId,
                        principalTable: "TradingStrategyVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TradingStrategySignalVetoes_OwnerUserId",
                table: "TradingStrategySignalVetoes",
                column: "OwnerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_TradingStrategySignalVetoes_TradingStrategyId",
                table: "TradingStrategySignalVetoes",
                column: "TradingStrategyId");

            migrationBuilder.CreateIndex(
                name: "IX_TradingStrategySignalVetoes_TradingStrategyId_EvaluatedAtUtc",
                table: "TradingStrategySignalVetoes",
                columns: new[] { "TradingStrategyId", "EvaluatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_TradingStrategySignalVetoes_TradingStrategyVersionId",
                table: "TradingStrategySignalVetoes",
                column: "TradingStrategyVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_TradingStrategySignalVetoes_TradingStrategyVersionId_SignalType_Symbol_Timeframe_IndicatorCloseTimeUtc_ReasonCode",
                table: "TradingStrategySignalVetoes",
                columns: new[] { "TradingStrategyVersionId", "SignalType", "Symbol", "Timeframe", "IndicatorCloseTimeUtc", "ReasonCode" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TradingStrategySignalVetoes");

            migrationBuilder.DropColumn(
                name: "RiskEvaluationJson",
                table: "TradingStrategySignals");

            migrationBuilder.DropColumn(
                name: "MaxLeverage",
                table: "RiskProfiles");
        }
    }
}
