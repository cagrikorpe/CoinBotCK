using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoinBot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTradingFeatureSnapshotQualityGuardsAndVolumeIndicators : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "KlingerOscillator",
                table: "TradingFeatureSnapshots",
                type: "decimal(38,18)",
                precision: 38,
                scale: 18,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "KlingerSignal",
                table: "TradingFeatureSnapshots",
                type: "decimal(38,18)",
                precision: 38,
                scale: 18,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "Mfi",
                table: "TradingFeatureSnapshots",
                type: "decimal(38,18)",
                precision: 38,
                scale: 18,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MissingFeatureSummary",
                table: "TradingFeatureSnapshots",
                type: "nvarchar(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "QualityReasonCode",
                table: "TradingFeatureSnapshots",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "KlingerOscillator",
                table: "TradingFeatureSnapshots");

            migrationBuilder.DropColumn(
                name: "KlingerSignal",
                table: "TradingFeatureSnapshots");

            migrationBuilder.DropColumn(
                name: "Mfi",
                table: "TradingFeatureSnapshots");

            migrationBuilder.DropColumn(
                name: "MissingFeatureSummary",
                table: "TradingFeatureSnapshots");

            migrationBuilder.DropColumn(
                name: "QualityReasonCode",
                table: "TradingFeatureSnapshots");
        }
    }
}
