using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoinBot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAiShadowDecisionOutcomes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AiShadowDecisionOutcomes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AiShadowDecisionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BotId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Symbol = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Timeframe = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    DecisionEvaluatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    HorizonKind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    HorizonValue = table.Column<int>(type: "int", nullable: false),
                    OutcomeState = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    OutcomeScore = table.Column<decimal>(type: "decimal(38,18)", precision: 38, scale: 18, nullable: true),
                    RealizedDirectionality = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    ConfidenceBucket = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    FutureDataAvailability = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ReferenceCandleCloseTimeUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    FutureCandleCloseTimeUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ReferenceClosePrice = table.Column<decimal>(type: "decimal(38,18)", precision: 38, scale: 18, nullable: true),
                    FutureClosePrice = table.Column<decimal>(type: "decimal(38,18)", precision: 38, scale: 18, nullable: true),
                    RealizedReturn = table.Column<decimal>(type: "decimal(38,18)", precision: 38, scale: 18, nullable: true),
                    FalsePositive = table.Column<bool>(type: "bit", nullable: false),
                    FalseNeutral = table.Column<bool>(type: "bit", nullable: false),
                    Overtrading = table.Column<bool>(type: "bit", nullable: false),
                    SuppressionCandidate = table.Column<bool>(type: "bit", nullable: false),
                    SuppressionAligned = table.Column<bool>(type: "bit", nullable: false),
                    ScoredAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    OwnerUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiShadowDecisionOutcomes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AiShadowDecisionOutcomes_AiShadowDecisions_AiShadowDecisionId",
                        column: x => x.AiShadowDecisionId,
                        principalTable: "AiShadowDecisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AiShadowDecisionOutcomes_AspNetUsers_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AiShadowDecisionOutcomes_TradingBots_BotId",
                        column: x => x.BotId,
                        principalTable: "TradingBots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AiShadowDecisionOutcomes_AiShadowDecisionId",
                table: "AiShadowDecisionOutcomes",
                column: "AiShadowDecisionId");

            migrationBuilder.CreateIndex(
                name: "IX_AiShadowDecisionOutcomes_BotId",
                table: "AiShadowDecisionOutcomes",
                column: "BotId");

            migrationBuilder.CreateIndex(
                name: "IX_AiShadowDecisionOutcomes_OwnerUserId",
                table: "AiShadowDecisionOutcomes",
                column: "OwnerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_AiShadowDecisionOutcomes_OwnerUserId_AiShadowDecisionId_HorizonKind_HorizonValue",
                table: "AiShadowDecisionOutcomes",
                columns: new[] { "OwnerUserId", "AiShadowDecisionId", "HorizonKind", "HorizonValue" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AiShadowDecisionOutcomes_OwnerUserId_HorizonKind_HorizonValue_ScoredAtUtc",
                table: "AiShadowDecisionOutcomes",
                columns: new[] { "OwnerUserId", "HorizonKind", "HorizonValue", "ScoredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AiShadowDecisionOutcomes_OwnerUserId_Symbol_Timeframe_ScoredAtUtc",
                table: "AiShadowDecisionOutcomes",
                columns: new[] { "OwnerUserId", "Symbol", "Timeframe", "ScoredAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AiShadowDecisionOutcomes");
        }
    }
}
