using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoinBot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMlFeatureSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MlFeatureSnapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Symbol = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Timeframe = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    StrategyKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    StrategyTemplateKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    StrategySchemaVersion = table.Column<int>(type: "int", nullable: true),
                    SignalDirection = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ScannerScore = table.Column<decimal>(type: "decimal(38,18)", precision: 38, scale: 18, nullable: true),
                    MarketScore = table.Column<decimal>(type: "decimal(38,18)", precision: 38, scale: 18, nullable: true),
                    StrategyScore = table.Column<int>(type: "int", nullable: true),
                    RiskPenalty = table.Column<decimal>(type: "decimal(38,18)", precision: 38, scale: 18, nullable: true),
                    TrendState = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    VolatilityState = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    LiquidityState = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    MarketFreshnessState = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    MarketFreshnessReason = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    MarketFreshnessSource = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    HistoricalFallbackState = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    PrivatePlaneFreshnessState = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    PrivatePlaneFreshnessReason = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    GuardDecision = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    GuardReasonCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ExecutionDecision = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    OrderSignalType = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    OrderState = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    SubmittedToBroker = table.Column<bool>(type: "bit", nullable: true),
                    ReduceOnly = table.Column<bool>(type: "bit", nullable: true),
                    PositionUnrealizedPnl = table.Column<decimal>(type: "decimal(38,18)", precision: 38, scale: 18, nullable: true),
                    PositionRealizedPnl = table.Column<decimal>(type: "decimal(38,18)", precision: 38, scale: 18, nullable: true),
                    FeatureCompletenessState = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    FeatureCompletenessSummary = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    SchemaVersion = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    CapturedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    FeatureAnchorTimeUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    MarketDataTimestampUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ExecutionEnvironment = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MlFeatureSnapshots", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MlFeatureSnapshots_ExecutionDecision",
                table: "MlFeatureSnapshots",
                column: "ExecutionDecision");

            migrationBuilder.CreateIndex(
                name: "IX_MlFeatureSnapshots_StrategyKey_CapturedAtUtc",
                table: "MlFeatureSnapshots",
                columns: new[] { "StrategyKey", "CapturedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_MlFeatureSnapshots_Symbol_Timeframe_CapturedAtUtc",
                table: "MlFeatureSnapshots",
                columns: new[] { "Symbol", "Timeframe", "CapturedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MlFeatureSnapshots");
        }
    }
}
