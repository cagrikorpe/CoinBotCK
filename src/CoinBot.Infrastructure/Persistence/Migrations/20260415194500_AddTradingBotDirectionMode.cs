using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoinBot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(ApplicationDbContext))]
    [Migration("20260415194500_AddTradingBotDirectionMode")]
    public partial class AddTradingBotDirectionMode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                IF COL_LENGTH(N'dbo.TradingBots', N'DirectionMode') IS NULL
                BEGIN
                    ALTER TABLE [dbo].[TradingBots]
                    ADD [DirectionMode] nvarchar(16) NOT NULL
                        CONSTRAINT [DF_TradingBots_DirectionMode] DEFAULT N'LongOnly';
                END
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                IF COL_LENGTH(N'dbo.TradingBots', N'DirectionMode') IS NOT NULL
                BEGIN
                    DECLARE @constraintName sysname;

                    SELECT @constraintName = [default_constraints].[name]
                    FROM [sys].[default_constraints]
                    INNER JOIN [sys].[columns]
                        ON [columns].[default_object_id] = [default_constraints].[object_id]
                    WHERE [default_constraints].[parent_object_id] = OBJECT_ID(N'dbo.TradingBots')
                        AND [columns].[name] = N'DirectionMode';

                    IF @constraintName IS NOT NULL
                    BEGIN
                        DECLARE @dropConstraintSql nvarchar(max) =
                            N'ALTER TABLE [dbo].[TradingBots] DROP CONSTRAINT ' + QUOTENAME(@constraintName) + N';';
                        EXEC sp_executesql @dropConstraintSql;
                    END

                    ALTER TABLE [dbo].[TradingBots] DROP COLUMN [DirectionMode];
                END
                """);
        }
    }
}
