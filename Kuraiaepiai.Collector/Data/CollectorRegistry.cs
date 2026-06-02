using Microsoft.Data.Sqlite;
using Dapper;
using System.IO;

namespace Kuraiaepiai.Collector.Data;

public class CollectorRegistry
{
    public string ConnectionString => "Data Source=clearapi_registry.db";
    public string StoragePath => Path.Combine(Directory.GetCurrentDirectory(), "Storage");

    public void Initialize()
    {
        // Ensure physical storage for swagger.json exists
        if (!Directory.Exists(StoragePath)) Directory.CreateDirectory(StoragePath);

        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        
        // 1. Core Registry (Added BaseUrl)
        connection.Execute(@"
            CREATE TABLE IF NOT EXISTS ApiRegistry (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                SystemName TEXT,
                ApiName TEXT,
                BaseUrl TEXT, 
                OwnershipJson TEXT,
                PackagesJson TEXT,
                CodeMapJson TEXT,
                LastUpdated DATETIME,
                UNIQUE(SystemName, ApiName)
            );");

        // 2. Individual API Governance Settings
        connection.Execute(@"
            CREATE TABLE IF NOT EXISTS ApiSettings (
                ApiId INTEGER PRIMARY KEY,
                PingIntervalMinutes INTEGER DEFAULT 5,
                TimeZone TEXT DEFAULT 'UTC',
                LogRetentionDays INTEGER DEFAULT 30,
                FOREIGN KEY(ApiId) REFERENCES ApiRegistry(Id) ON DELETE CASCADE
            );");

        // 3. Health History Logs
        connection.Execute(@"
            CREATE TABLE IF NOT EXISTS HealthLogs (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ApiId INTEGER,
                Timestamp DATETIME,
                Status TEXT,
                ResponseTimeMs INTEGER,
                Details TEXT,
                FOREIGN KEY(ApiId) REFERENCES ApiRegistry(Id) ON DELETE CASCADE
            );");
    }

    public SqliteConnection CreateConnection() => new SqliteConnection(ConnectionString);
}