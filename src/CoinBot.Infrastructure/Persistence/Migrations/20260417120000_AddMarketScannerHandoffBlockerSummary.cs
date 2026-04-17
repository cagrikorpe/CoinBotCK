using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoinBot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(ApplicationDbContext))]
    [Migration("20260417120000_AddMarketScannerHandoffBlockerSummary")]
    public partial class AddMarketScannerHandoffBlockerSummary : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BlockerSummary",
                table: "MarketScannerHandoffAttempts",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE MarketScannerHandoffAttempts
                SET BlockerSummary =
                    CASE
                        WHEN ExecutionRequestStatus = 'Prepared'
                            THEN 'Allowed: execution request prepared.'
                        WHEN NULLIF(LTRIM(RTRIM(BlockerCode)), '') IS NOT NULL
                             AND NULLIF(LTRIM(RTRIM(BlockerDetail)), '') IS NOT NULL
                            THEN LEFT(LTRIM(RTRIM(BlockerCode)) + ': ' + LTRIM(RTRIM(BlockerDetail)), 256)
                        WHEN NULLIF(LTRIM(RTRIM(BlockerCode)), '') IS NOT NULL
                            THEN LEFT(LTRIM(RTRIM(BlockerCode)) + ': scanner handoff blocked execution.', 256)
                        ELSE 'Blocked: scanner handoff blocked execution.'
                    END
                WHERE BlockerSummary IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BlockerSummary",
                table: "MarketScannerHandoffAttempts");
        }
    }
}