using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoinBot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTradingFeatureSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TradingFeatureSnapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BotId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ExchangeAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    StrategyKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Symbol = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Timeframe = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    EvaluatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    MarketDataTimestampUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    FeatureVersion = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    SnapshotState = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    MarketDataReasonCode = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    SampleCount = table.Column<int>(type: "int", nullable: false),
                    RequiredSampleCount = table.Column<int>(type: "int", nullable: false),
                    ReferencePrice = table.Column<decimal>(type: "decimal(38,18)", precision: 38, scale: 18, nullable: true),
                    Ema20 = table.Column<decimal>(type: "decimal(38,18)", precision: 38, scale: 18, nullable: true),
                    Ema50 = table.Column<decimal>(type: "decimal(38,18)", precision: 38, scale: 18, nullable: true),
                    Ema200 = table.Column<decimal>(type: "decimal(38,18)", precision: 38, scale: 18, nullable: true),
                    Alma = table.Column<decimal>(type: "decimal(38,18)", precision: 38, scale: 18, nullable: true),
                    Frama = table.Column<decimal>(type: "decimal(38,18)", precision: 38, scale: 18, nullable: true),
                    Rsi = table.Column<decimal>(type: "decimal(38,18)", precision: 38, scale: 18, nullable: true),
                    MacdLine = table.Column<decimal>(type: "decimal(38,18)", precision: 38, scale: 18, nullable: true),
                    MacdSignal = table.Column<decimal>(type: "decimal(38,18)", precision: 38, scale: 18, nullable: true),
                    MacdHistogram = table.Column<decimal>(type: "decimal(38,18)", precision: 38, scale: 18, nullable: true),
                    KdjK = table.Column<decimal>(type: "decimal(38,18)", precision: 38, scale: 18, nullable: true),
                    KdjD = table.Column<decimal>(type: "decimal(38,18)", precision: 38, scale: 18, nullable: true),
                    KdjJ = table.Column<decimal>(type: "decimal(38,18)", precision: 38, scale: 18, nullable: true),
                    FisherTransform = table.Column<decimal>(type: "decimal(38,18)", precision: 38, scale: 18, nullable: true),
                    Atr = table.Column<decimal>(type: "decimal(38,18)", precision: 38, scale: 18, nullable: true),
                    BollingerPercentB = table.Column<decimal>(type: "decimal(38,18)", precision: 38, scale: 18, nullable: true),
                    BollingerBandWidth = table.Column<decimal>(type: "decimal(38,18)", precision: 38, scale: 18, nullable: true),
                    KeltnerChannelRelation = table.Column<decimal>(type: "decimal(38,18)", precision: 38, scale: 18, nullable: true),
                    PmaxValue = table.Column<decimal>(type: "decimal(38,18)", precision: 38, scale: 18, nullable: true),
                    ChandelierExit = table.Column<decimal>(type: "decimal(38,18)", precision: 38, scale: 18, nullable: true),
                    VolumeSpikeRatio = table.Column<decimal>(type: "decimal(38,18)", precision: 38, scale: 18, nullable: true),
                    RelativeVolume = table.Column<decimal>(type: "decimal(38,18)", precision: 38, scale: 18, nullable: true),
                    Obv = table.Column<decimal>(type: "decimal(38,18)", precision: 38, scale: 18, nullable: true),
                    Plane = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    TradingMode = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    HasOpenPosition = table.Column<bool>(type: "bit", nullable: false),
                    IsInCooldown = table.Column<bool>(type: "bit", nullable: false),
                    LastVetoReasonCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    LastDecisionOutcome = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    LastDecisionCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    LastExecutionState = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    LastFailureCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    FeatureSummary = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: false),
                    TopSignalHints = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: false),
                    PrimaryRegime = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    MomentumBias = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    VolatilityState = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    NormalizationMeta = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    OwnerUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TradingFeatureSnapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TradingFeatureSnapshots_AspNetUsers_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TradingFeatureSnapshots_ExchangeAccounts_ExchangeAccountId",
                        column: x => x.ExchangeAccountId,
                        principalTable: "ExchangeAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TradingFeatureSnapshots_TradingBots_BotId",
                        column: x => x.BotId,
                        principalTable: "TradingBots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TradingFeatureSnapshots_BotId",
                table: "TradingFeatureSnapshots",
                column: "BotId");

            migrationBuilder.CreateIndex(
                name: "IX_TradingFeatureSnapshots_ExchangeAccountId",
                table: "TradingFeatureSnapshots",
                column: "ExchangeAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_TradingFeatureSnapshots_OwnerUserId",
                table: "TradingFeatureSnapshots",
                column: "OwnerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_TradingFeatureSnapshots_OwnerUserId_BotId_Symbol_Timeframe_EvaluatedAtUtc",
                table: "TradingFeatureSnapshots",
                columns: new[] { "OwnerUserId", "BotId", "Symbol", "Timeframe", "EvaluatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_TradingFeatureSnapshots_OwnerUserId_Symbol_Timeframe_EvaluatedAtUtc",
                table: "TradingFeatureSnapshots",
                columns: new[] { "OwnerUserId", "Symbol", "Timeframe", "EvaluatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TradingFeatureSnapshots");
        }
    }
}
