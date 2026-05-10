using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoinBot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMlFeatureSnapshotBaselineScores : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BaselineScoreSummary",
                table: "MlFeatureSnapshots",
                type: "nvarchar(512)",
                maxLength: 512,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<decimal>(
                name: "CombinedBaselineScore",
                table: "MlFeatureSnapshots",
                type: "decimal(38,18)",
                precision: 38,
                scale: 18,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "DrawdownPenalty",
                table: "MlFeatureSnapshots",
                type: "decimal(38,18)",
                precision: 38,
                scale: 18,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "FeatureCompletenessScore",
                table: "MlFeatureSnapshots",
                type: "decimal(38,18)",
                precision: 38,
                scale: 18,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "LiquidityScore",
                table: "MlFeatureSnapshots",
                type: "decimal(38,18)",
                precision: 38,
                scale: 18,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "RecentPerformanceScore",
                table: "MlFeatureSnapshots",
                type: "decimal(38,18)",
                precision: 38,
                scale: 18,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "SampleQualityScore",
                table: "MlFeatureSnapshots",
                type: "decimal(38,18)",
                precision: 38,
                scale: 18,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "SignalConfidenceScore",
                table: "MlFeatureSnapshots",
                type: "decimal(38,18)",
                precision: 38,
                scale: 18,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "TrendAlignmentScore",
                table: "MlFeatureSnapshots",
                type: "decimal(38,18)",
                precision: 38,
                scale: 18,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "VolatilityScore",
                table: "MlFeatureSnapshots",
                type: "decimal(38,18)",
                precision: 38,
                scale: 18,
                nullable: false,
                defaultValue: 0m);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BaselineScoreSummary",
                table: "MlFeatureSnapshots");

            migrationBuilder.DropColumn(
                name: "CombinedBaselineScore",
                table: "MlFeatureSnapshots");

            migrationBuilder.DropColumn(
                name: "DrawdownPenalty",
                table: "MlFeatureSnapshots");

            migrationBuilder.DropColumn(
                name: "FeatureCompletenessScore",
                table: "MlFeatureSnapshots");

            migrationBuilder.DropColumn(
                name: "LiquidityScore",
                table: "MlFeatureSnapshots");

            migrationBuilder.DropColumn(
                name: "RecentPerformanceScore",
                table: "MlFeatureSnapshots");

            migrationBuilder.DropColumn(
                name: "SampleQualityScore",
                table: "MlFeatureSnapshots");

            migrationBuilder.DropColumn(
                name: "SignalConfidenceScore",
                table: "MlFeatureSnapshots");

            migrationBuilder.DropColumn(
                name: "TrendAlignmentScore",
                table: "MlFeatureSnapshots");

            migrationBuilder.DropColumn(
                name: "VolatilityScore",
                table: "MlFeatureSnapshots");
        }
    }
}
