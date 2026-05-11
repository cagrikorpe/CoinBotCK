using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoinBot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMlFeatureSnapshotShadowScoringEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FeatureSchemaVersion",
                table: "MlFeatureSnapshots",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "IsDecisionInfluential",
                table: "MlFeatureSnapshots",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<decimal>(
                name: "MlConfidence",
                table: "MlFeatureSnapshots",
                type: "decimal(38,18)",
                precision: 38,
                scale: 18,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "MlShadowDecision",
                table: "MlFeatureSnapshots",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<decimal>(
                name: "MlShadowScore",
                table: "MlFeatureSnapshots",
                type: "decimal(38,18)",
                precision: 38,
                scale: 18,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ModelVersion",
                table: "MlFeatureSnapshots",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ReasonSummary",
                table: "MlFeatureSnapshots",
                type: "nvarchar(512)",
                maxLength: 512,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FeatureSchemaVersion",
                table: "MlFeatureSnapshots");

            migrationBuilder.DropColumn(
                name: "IsDecisionInfluential",
                table: "MlFeatureSnapshots");

            migrationBuilder.DropColumn(
                name: "MlConfidence",
                table: "MlFeatureSnapshots");

            migrationBuilder.DropColumn(
                name: "MlShadowDecision",
                table: "MlFeatureSnapshots");

            migrationBuilder.DropColumn(
                name: "MlShadowScore",
                table: "MlFeatureSnapshots");

            migrationBuilder.DropColumn(
                name: "ModelVersion",
                table: "MlFeatureSnapshots");

            migrationBuilder.DropColumn(
                name: "ReasonSummary",
                table: "MlFeatureSnapshots");
        }
    }
}
