using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoinBot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAiShadowDecisions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AiShadowDecisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BotId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ExchangeAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TradingStrategyId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TradingStrategyVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    StrategySignalId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    StrategySignalVetoId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    FeatureSnapshotId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    StrategyDecisionTraceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    HypotheticalDecisionTraceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    StrategyKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Symbol = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Timeframe = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    EvaluatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    MarketDataTimestampUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    FeatureVersion = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    StrategyDirection = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    StrategyConfidenceScore = table.Column<int>(type: "int", nullable: true),
                    StrategyDecisionOutcome = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    StrategyDecisionCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    StrategySummary = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                    AiDirection = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    AiConfidence = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: false),
                    AiReasonSummary = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    AiProviderName = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    AiProviderModel = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    AiLatencyMs = table.Column<int>(type: "int", nullable: false),
                    AiIsFallback = table.Column<bool>(type: "bit", nullable: false),
                    AiFallbackReason = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    RiskVetoPresent = table.Column<bool>(type: "bit", nullable: false),
                    RiskVetoReason = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    RiskVetoSummary = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                    PilotSafetyBlocked = table.Column<bool>(type: "bit", nullable: false),
                    PilotSafetyReason = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    PilotSafetySummary = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                    TradingMode = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Plane = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    FinalAction = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    HypotheticalSubmitAllowed = table.Column<bool>(type: "bit", nullable: false),
                    HypotheticalBlockReason = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    HypotheticalBlockSummary = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                    NoSubmitReason = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    FeatureSummary = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                    AgreementState = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    OwnerUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiShadowDecisions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AiShadowDecisions_AspNetUsers_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AiShadowDecisions_ExchangeAccounts_ExchangeAccountId",
                        column: x => x.ExchangeAccountId,
                        principalTable: "ExchangeAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AiShadowDecisions_TradingBots_BotId",
                        column: x => x.BotId,
                        principalTable: "TradingBots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AiShadowDecisions_TradingFeatureSnapshots_FeatureSnapshotId",
                        column: x => x.FeatureSnapshotId,
                        principalTable: "TradingFeatureSnapshots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AiShadowDecisions_BotId",
                table: "AiShadowDecisions",
                column: "BotId");

            migrationBuilder.CreateIndex(
                name: "IX_AiShadowDecisions_CorrelationId",
                table: "AiShadowDecisions",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_AiShadowDecisions_ExchangeAccountId",
                table: "AiShadowDecisions",
                column: "ExchangeAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_AiShadowDecisions_FeatureSnapshotId",
                table: "AiShadowDecisions",
                column: "FeatureSnapshotId");

            migrationBuilder.CreateIndex(
                name: "IX_AiShadowDecisions_OwnerUserId",
                table: "AiShadowDecisions",
                column: "OwnerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_AiShadowDecisions_OwnerUserId_BotId_Symbol_Timeframe_EvaluatedAtUtc",
                table: "AiShadowDecisions",
                columns: new[] { "OwnerUserId", "BotId", "Symbol", "Timeframe", "EvaluatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AiShadowDecisions_OwnerUserId_Symbol_Timeframe_EvaluatedAtUtc",
                table: "AiShadowDecisions",
                columns: new[] { "OwnerUserId", "Symbol", "Timeframe", "EvaluatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AiShadowDecisions_StrategySignalId",
                table: "AiShadowDecisions",
                column: "StrategySignalId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AiShadowDecisions");
        }
    }
}
