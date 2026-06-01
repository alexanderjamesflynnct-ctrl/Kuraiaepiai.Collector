using Microsoft.Data.Sqlite;
using Dapper;

namespace Kuraiaepiai.Collector.Data;

public class CollectorRegistry
{
    public string ConnectionString => "Data Source=clearapi_registry.db";
    public string StoragePath => Path.Combine(Directory.GetCurrentDirectory(), "Storage");

    public void Initialize()
    {
        if (!Directory.Exists(StoragePath)) Directory.CreateDirectory(StoragePath);

        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        connection.Execute(@"
            CREATE TABLE IF NOT EXISTS ApiRegistry (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                SystemName TEXT,
                ApiName TEXT,
                OwnershipJson TEXT,
                PackagesJson TEXT,
                CodeMapJson TEXT,
                LastUpdated DATETIME,
                UNIQUE(SystemName, ApiName)
            )");
    }

    public SqliteConnection CreateConnection() => new SqliteConnection(ConnectionString);
}