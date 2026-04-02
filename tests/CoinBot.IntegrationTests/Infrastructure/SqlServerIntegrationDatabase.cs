using Microsoft.Data.SqlClient;

namespace CoinBot.IntegrationTests.Infrastructure;

internal static class SqlServerIntegrationDatabase
{
    private const string ConnectionStringEnvironmentVariable = "COINBOT_INTEGRATION_SQLSERVER_CONNECTION_STRING";
    private const string DefaultConnectionString = "Server=(localdb)\\MSSQLLocalDB;Integrated Security=True;TrustServerCertificate=True;MultipleActiveResultSets=True";

    public static string ResolveConnectionString(string databaseName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);

        var configuredConnectionString = Environment.GetEnvironmentVariable(ConnectionStringEnvironmentVariable);
        var rawConnectionString = string.IsNullOrWhiteSpace(configuredConnectionString)
            ? DefaultConnectionString
            : configuredConnectionString.Replace("{Database}", databaseName, StringComparison.OrdinalIgnoreCase);
        var builder = new SqlConnectionStringBuilder(rawConnectionString)
        {
            InitialCatalog = databaseName,
            TrustServerCertificate = true,
            MultipleActiveResultSets = true
        };

        EnsureDatabaseExists(builder);
        return builder.ConnectionString;
    }

    public static async Task CleanupDatabaseAsync(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);

        if (string.IsNullOrWhiteSpace(builder.InitialCatalog))
        {
            return;
        }

        var databaseName = builder.InitialCatalog;
        builder.InitialCatalog = "master";

        await using var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            IF DB_ID(@databaseName) IS NOT NULL
            BEGIN
                DECLARE @quotedDatabaseName nvarchar(258) = QUOTENAME(@databaseName);
                DECLARE @sql nvarchar(max) = N'ALTER DATABASE ' + @quotedDatabaseName + N' SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE ' + @quotedDatabaseName + N';';
                EXEC(@sql);
            END
            """;
        command.Parameters.AddWithValue("@databaseName", databaseName);
        await command.ExecuteNonQueryAsync();
    }

    private static void EnsureDatabaseExists(SqlConnectionStringBuilder builder)
    {
        var databaseName = builder.InitialCatalog;
        var masterBuilder = new SqlConnectionStringBuilder(builder.ConnectionString)
        {
            InitialCatalog = "master"
        };

        using var connection = new SqlConnection(masterBuilder.ConnectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            IF DB_ID(@databaseName) IS NULL
            BEGIN
                DECLARE @quotedDatabaseName nvarchar(258) = QUOTENAME(@databaseName);
                DECLARE @sql nvarchar(max) = N'CREATE DATABASE ' + @quotedDatabaseName + N';';
                EXEC(@sql);
            END
            """;
        command.Parameters.AddWithValue("@databaseName", databaseName);
        command.ExecuteNonQuery();
    }
}
