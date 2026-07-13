using Microsoft.Data.Sqlite;

using Xunit;

namespace AgentStudio.Tests;

public sealed class DependencySecuritySmokeTests
{
    [Fact]
    public void BundledSqlite_IsPatchedAndCanExecuteQueries()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "select sqlite_version()";
        var versionText = Assert.IsType<string>(command.ExecuteScalar());

        Assert.True(
            Version.Parse(versionText) >= new Version(3, 50, 2),
            $"Expected SQLite 3.50.2 or newer, but loaded {versionText}.");
    }
}
