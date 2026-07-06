using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Api.Tests;

[TestClass]
public sealed class StorageSchemaTests
{
    [TestMethod]
    public void EnsureCreated_UsesCompactBlobAndIntegerColumnTypes()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"messaging-2-schema-{Guid.NewGuid():N}.db");

        try
        {
            DbContextOptions<Context.AppContext> options = new DbContextOptionsBuilder<Context.AppContext>()
                .UseSqlite($"Data Source={dbPath}")
                .Options;

            using (Context.AppContext dbContext = new(options))
            {
                dbContext.Database.EnsureCreated();
            }

            using SqliteConnection connection = new($"Data Source={dbPath}");
            connection.Open();

            AssertColumnType(connection, "EncryptedMessages", "RoomHash", "BLOB");
            AssertColumnType(connection, "EncryptedMessages", "SenderPublicKey", "BLOB");
            AssertColumnType(connection, "EncryptedMessages", "Nonce", "BLOB");
            AssertColumnType(connection, "EncryptedMessages", "CypherText", "BLOB");
            AssertColumnType(connection, "EncryptedMessages", "Signature", "BLOB");
            AssertColumnType(connection, "MessageShards", "RoomHash", "BLOB");
            AssertColumnType(connection, "MessageShards", "StoredAtUtc", "INTEGER");
            AssertColumnType(connection, "RoomClocks", "RoomHash", "BLOB");
            AssertColumnType(connection, "KnownServers", "FirstSeenAtUtc", "INTEGER");
            AssertColumnType(connection, "KnownServers", "LastSeenAtUtc", "INTEGER");
        }
        finally
        {
            try
            {
                File.Delete(dbPath);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static void AssertColumnType(SqliteConnection connection, string tableName, string columnName, string expectedType)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{tableName}\")";

        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (!string.Equals(reader.GetString(1), columnName, StringComparison.Ordinal))
            {
                continue;
            }

            Assert.AreEqual(expectedType, reader.GetString(2), $"{tableName}.{columnName} type mismatch.");
            return;
        }

        Assert.Fail($"Column '{columnName}' was not found on table '{tableName}'.");
    }
}
