using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoinBot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAiShadowDecisionAdvisoryFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "AiAdvisoryScore",
                table: "AiShadowDecisions",
                type: "decimal(18,8)",
                precision: 18,
                scale: 8,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "AiContributionSummary",
                table: "AiShadowDecisions",
                type: "nvarchar(1024)",
                maxLength: 1024,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AiAdvisoryScore",
                table: "AiShadowDecisions");

            migrationBuilder.DropColumn(
                name: "AiContributionSummary",
                table: "AiShadowDecisions");
        }
    }
}
