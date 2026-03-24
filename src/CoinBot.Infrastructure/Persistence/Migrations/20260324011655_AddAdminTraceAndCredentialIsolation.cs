using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoinBot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAdminTraceAndCredentialIsolation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ApiCredentials",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ExchangeAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OwnerUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    ApiKeyCiphertext = table.Column<string>(type: "nvarchar(max)", maxLength: 4096, nullable: false),
                    ApiSecretCiphertext = table.Column<string>(type: "nvarchar(max)", maxLength: 4096, nullable: false),
                    CredentialFingerprint = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    KeyVersion = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    EncryptedBlobVersion = table.Column<int>(type: "int", nullable: false),
                    ValidationStatus = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    PermissionSummary = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    StoredAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastValidatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastFailureReason = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApiCredentials", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DecisionTraces",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StrategySignalId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    DecisionId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Symbol = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Timeframe = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    StrategyVersion = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    SignalType = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    RiskScore = table.Column<int>(type: "int", nullable: true),
                    DecisionOutcome = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    VetoReasonCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    LatencyMs = table.Column<int>(type: "int", nullable: false),
                    SnapshotJson = table.Column<string>(type: "nvarchar(max)", maxLength: 8192, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DecisionTraces", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ExecutionTraces",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ExecutionOrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ExecutionAttemptId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    CommandId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Provider = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Endpoint = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    RequestMasked = table.Column<string>(type: "nvarchar(max)", maxLength: 4096, nullable: true),
                    ResponseMasked = table.Column<string>(type: "nvarchar(max)", maxLength: 4096, nullable: true),
                    HttpStatusCode = table.Column<int>(type: "int", nullable: true),
                    ExchangeCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    LatencyMs = table.Column<int>(type: "int", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExecutionTraces", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UserExecutionOverrides",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    AllowedSymbolsCsv = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    DeniedSymbolsCsv = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    LeverageCap = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: true),
                    MaxOrderSize = table.Column<decimal>(type: "decimal(38,18)", precision: 38, scale: 18, nullable: true),
                    MaxDailyTrades = table.Column<int>(type: "int", nullable: true),
                    ReduceOnly = table.Column<bool>(type: "bit", nullable: false),
                    SessionDisabled = table.Column<bool>(type: "bit", nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserExecutionOverrides", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ApiCredentialValidations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ApiCredentialId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ExchangeAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OwnerUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    IsKeyValid = table.Column<bool>(type: "bit", nullable: false),
                    CanTrade = table.Column<bool>(type: "bit", nullable: false),
                    CanWithdraw = table.Column<bool>(type: "bit", nullable: false),
                    SupportsSpot = table.Column<bool>(type: "bit", nullable: false),
                    SupportsFutures = table.Column<bool>(type: "bit", nullable: false),
                    EnvironmentScope = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    IsEnvironmentMatch = table.Column<bool>(type: "bit", nullable: false),
                    HasTimestampSkew = table.Column<bool>(type: "bit", nullable: false),
                    HasIpRestrictionIssue = table.Column<bool>(type: "bit", nullable: false),
                    ValidationStatus = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    PermissionSummary = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    FailureReason = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    ValidatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApiCredentialValidations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ApiCredentialValidations_ApiCredentials_ApiCredentialId",
                        column: x => x.ApiCredentialId,
                        principalTable: "ApiCredentials",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ApiCredentials_ExchangeAccountId",
                table: "ApiCredentials",
                column: "ExchangeAccountId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApiCredentials_ValidationStatus_LastValidatedAtUtc",
                table: "ApiCredentials",
                columns: new[] { "ValidationStatus", "LastValidatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ApiCredentialValidations_ApiCredentialId_ValidatedAtUtc",
                table: "ApiCredentialValidations",
                columns: new[] { "ApiCredentialId", "ValidatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ApiCredentialValidations_ExchangeAccountId_ValidatedAtUtc",
                table: "ApiCredentialValidations",
                columns: new[] { "ExchangeAccountId", "ValidatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_DecisionTraces_CorrelationId",
                table: "DecisionTraces",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_DecisionTraces_DecisionId",
                table: "DecisionTraces",
                column: "DecisionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DecisionTraces_StrategySignalId",
                table: "DecisionTraces",
                column: "StrategySignalId");

            migrationBuilder.CreateIndex(
                name: "IX_DecisionTraces_UserId_CreatedAtUtc",
                table: "DecisionTraces",
                columns: new[] { "UserId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ExecutionTraces_CommandId_CreatedAtUtc",
                table: "ExecutionTraces",
                columns: new[] { "CommandId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ExecutionTraces_CorrelationId",
                table: "ExecutionTraces",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_ExecutionTraces_ExecutionAttemptId",
                table: "ExecutionTraces",
                column: "ExecutionAttemptId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExecutionTraces_ExecutionOrderId",
                table: "ExecutionTraces",
                column: "ExecutionOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_ExecutionTraces_UserId_CreatedAtUtc",
                table: "ExecutionTraces",
                columns: new[] { "UserId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_UserExecutionOverrides_UserId",
                table: "UserExecutionOverrides",
                column: "UserId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ApiCredentialValidations");

            migrationBuilder.DropTable(
                name: "DecisionTraces");

            migrationBuilder.DropTable(
                name: "ExecutionTraces");

            migrationBuilder.DropTable(
                name: "UserExecutionOverrides");

            migrationBuilder.DropTable(
                name: "ApiCredentials");
        }
    }
}
