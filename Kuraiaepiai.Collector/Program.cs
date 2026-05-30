using Microsoft.Data.Sqlite;
using Dapper;
using System.Text.Json;
using Microsoft.Extensions.FileProviders;

var builder = WebApplication.CreateBuilder(args);

// ==========================================
// 1. SERVICE CONFIGURATION (Must be before builder.Build)
// ==========================================

// Enable CORS so the React UI (port 5173) can talk to this API
builder.Services.AddCors(options => {
    options.AddPolicy("AllowKuraiaepiaiUI", policy => {
        policy.AllowAnyOrigin()
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

var app = builder.Build();

// ==========================================
// 2. INITIALIZATION (Database & Storage)
// ==========================================

const string ConnectionString = "Data Source=clearapi_registry.db";
var storagePath = Path.Combine(Directory.GetCurrentDirectory(), "Storage");

// Ensure physical storage exists for swagger files
if (!Directory.Exists(storagePath)) 
{
    Directory.CreateDirectory(storagePath);
    Console.WriteLine("[クリアエーピーアイ] Created Storage directory.");
}

// Initialize SQLite Schema
using (var connection = new SqliteConnection(ConnectionString))
{
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

// ==========================================
// 3. MIDDLEWARE PIPELINE
// ==========================================

app.UseCors("AllowKuraiaepiaiUI");

// Serve the Storage folder as static files so UI can fetch swagger.json
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(storagePath),
    RequestPath = "/docs"
});

// ==========================================
// 4. API ENDPOINTS
// ==========================================

// THE COLLECTOR: Called by Source APIs via /clearapi/push
app.MapPost("/api/collect", async (HttpContext context) =>
{
    try 
    {
        using var reader = new StreamReader(context.Request.Body);
        var body = await reader.ReadToEndAsync();
        var report = JsonDocument.Parse(body);
        var root = report.RootElement;

        // Helper: Case-Insensitive property finder for robust JSON parsing
        JsonElement GetProp(JsonElement element, string targetName) {
            foreach (var prop in element.EnumerateObject()) {
                if (string.Equals(prop.Name, targetName, StringComparison.OrdinalIgnoreCase))
                    return prop.Value;
            }
            throw new KeyNotFoundException($"Key '{targetName}' not found in JSON payload.");
        }

        // Extract metadata blocks
        var ownership = GetProp(root, "ownership");
        var packages = GetProp(root, "packages");
        var codeMap = GetProp(root, "codeMap");
        var swagger = GetProp(root, "swagger");
        
        var systemName = GetProp(ownership, "SystemName").GetString() ?? "UnknownSystem";
        var apiName = GetProp(ownership, "APIName").GetString() ?? "UnknownAPI";

        // Save physical swagger.json
        var apiDir = Path.Combine(storagePath, systemName, apiName);
        if (!Directory.Exists(apiDir)) Directory.CreateDirectory(apiDir);
        await File.WriteAllTextAsync(Path.Combine(apiDir, "swagger.json"), swagger.GetRawText());

        // Update Registry Database
        using (var connection = new SqliteConnection(ConnectionString))
        {
            var sql = @"
                INSERT INTO ApiRegistry (SystemName, ApiName, OwnershipJson, PackagesJson, CodeMapJson, LastUpdated)
                VALUES (@SystemName, @ApiName, @Ownership, @Packages, @CodeMap, @LastUpdated)
                ON CONFLICT(SystemName, ApiName) DO UPDATE SET
                    OwnershipJson = excluded.OwnershipJson,
                    PackagesJson = excluded.PackagesJson,
                    CodeMapJson = excluded.CodeMapJson,
                    LastUpdated = excluded.LastUpdated";

            await connection.ExecuteAsync(sql, new {
                SystemName = systemName,
                ApiName = apiName,
                Ownership = ownership.GetRawText(),
                Packages = packages.GetRawText(),
                CodeMap = codeMap.GetRawText(),
                LastUpdated = DateTime.Now
            });
        }

        Console.WriteLine($"[クリアエーピーアイ] Sync'd Success: {systemName} -> {apiName}");
        return Results.Ok(new { message = "Sync Successful" });
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[クリアエーピーアイ] Collection Error: {ex.Message}");
        return Results.Problem(ex.Message);
    }
});

// GET TREE: For the React Sidebar
app.MapGet("/api/tree", async () =>
{
    using var connection = new SqliteConnection(ConnectionString);
    var items = await connection.QueryAsync("SELECT SystemName, ApiName FROM ApiRegistry");
    return items.GroupBy(x => (string)x.SystemName)
                .Select(g => new { system = g.Key, apis = g.Select(x => x.ApiName) });
});

// GET DETAILS: For the React Modals and Codemap
app.MapGet("/api/details/{system}/{api}", async (string system, string api) =>
{
    using var connection = new SqliteConnection(ConnectionString);
    var result = await connection.QueryFirstOrDefaultAsync(
        "SELECT * FROM ApiRegistry WHERE SystemName = @system AND ApiName = @api", 
        new { system, api });
    return result != null ? Results.Ok(result) : Results.NotFound();
});

// ==========================================
// 5. RUN
// ==========================================

Console.WriteLine("クリアエーピーアイ (Kuriāēpīai) Collector is starting on port 8000...");
app.Run("http://localhost:8000");