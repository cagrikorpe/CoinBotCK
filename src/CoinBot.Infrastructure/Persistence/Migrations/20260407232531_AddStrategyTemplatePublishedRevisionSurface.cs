using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoinBot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddStrategyTemplatePublishedRevisionSurface : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "PublishedTradingStrategyTemplateRevisionId",
                table: "TradingStrategyTemplates",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_TradingStrategyTemplates_PublishedTradingStrategyTemplateRevisionId",
                table: "TradingStrategyTemplates",
                column: "PublishedTradingStrategyTemplateRevisionId");

            migrationBuilder.AddForeignKey(
                name: "FK_TradingStrategyTemplates_TradingStrategyTemplateRevisions_PublishedTradingStrategyTemplateRevisionId",
                table: "TradingStrategyTemplates",
                column: "PublishedTradingStrategyTemplateRevisionId",
                principalTable: "TradingStrategyTemplateRevisions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_TradingStrategyTemplates_TradingStrategyTemplateRevisions_PublishedTradingStrategyTemplateRevisionId",
                table: "TradingStrategyTemplates");

            migrationBuilder.DropIndex(
                name: "IX_TradingStrategyTemplates_PublishedTradingStrategyTemplateRevisionId",
                table: "TradingStrategyTemplates");

            migrationBuilder.DropColumn(
                name: "PublishedTradingStrategyTemplateRevisionId",
                table: "TradingStrategyTemplates");
        }
    }
}
