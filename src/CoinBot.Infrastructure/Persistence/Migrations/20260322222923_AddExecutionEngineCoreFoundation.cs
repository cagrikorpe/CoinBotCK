using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoinBot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddExecutionEngineCoreFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ExecutionOrders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TradingStrategyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TradingStrategyVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StrategySignalId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SignalType = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    BotId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ExchangeAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    StrategyKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Symbol = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Timeframe = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    BaseAsset = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    QuoteAsset = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Side = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    OrderType = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Quantity = table.Column<decimal>(type: "decimal(38,18)", precision: 38, scale: 18, nullable: false),
                    Price = table.Column<decimal>(type: "decimal(38,18)", precision: 38, scale: 18, nullable: false),
                    ExecutionEnvironment = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ExecutorKind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    State = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    RootCorrelationId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ParentCorrelationId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    ExternalOrderId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    FailureCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    FailureDetail = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    SubmittedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastStateChangedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    OwnerUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExecutionOrders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExecutionOrders_AspNetUsers_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ExecutionOrderTransitions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ExecutionOrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SequenceNumber = table.Column<int>(type: "int", nullable: false),
                    State = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    EventCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Detail = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ParentCorrelationId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    OccurredAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    OwnerUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExecutionOrderTransitions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExecutionOrderTransitions_AspNetUsers_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ExecutionOrderTransitions_ExecutionOrders_ExecutionOrderId",
                        column: x => x.ExecutionOrderId,
                        principalTable: "ExecutionOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ExecutionOrders_BotId",
                table: "ExecutionOrders",
                column: "BotId");

            migrationBuilder.CreateIndex(
                name: "IX_ExecutionOrders_ExchangeAccountId",
                table: "ExecutionOrders",
                column: "ExchangeAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_ExecutionOrders_OwnerUserId",
                table: "ExecutionOrders",
                column: "OwnerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ExecutionOrders_OwnerUserId_IdempotencyKey",
                table: "ExecutionOrders",
                columns: new[] { "OwnerUserId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExecutionOrders_OwnerUserId_State_LastStateChangedAtUtc",
                table: "ExecutionOrders",
                columns: new[] { "OwnerUserId", "State", "LastStateChangedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ExecutionOrders_StrategySignalId",
                table: "ExecutionOrders",
                column: "StrategySignalId");

            migrationBuilder.CreateIndex(
                name: "IX_ExecutionOrderTransitions_ExecutionOrderId_OccurredAtUtc",
                table: "ExecutionOrderTransitions",
                columns: new[] { "ExecutionOrderId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ExecutionOrderTransitions_ExecutionOrderId_SequenceNumber",
                table: "ExecutionOrderTransitions",
                columns: new[] { "ExecutionOrderId", "SequenceNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExecutionOrderTransitions_OwnerUserId",
                table: "ExecutionOrderTransitions",
                column: "OwnerUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ExecutionOrderTransitions");

            migrationBuilder.DropTable(
                name: "ExecutionOrders");
        }
    }
}
