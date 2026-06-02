using Microsoft.AspNetCore.Mvc;
using Kuraiaepiai.Collector.Data;
using Kuraiaepiai.Collector.Services;
using Dapper;
using System.Text.Json;
using System.Text;
using System.Net;

namespace Kuraiaepiai.Collector.Controllers;

[ApiController]
[Route("api")]
[Tags("Collector-Core")]
public class CollectorController : ControllerBase
{
    private readonly CollectorRegistry _registry;
    private readonly IHealthMonitor _pinger;

    public CollectorController(CollectorRegistry registry, IHealthMonitor pinger)
    {
        _registry = registry;
        _pinger = pinger;
    }
// 1. MANUAL PING: Triggered by the "Ping Now" button in the UI
    [HttpPost("health/ping/{id:int}")]
    public async Task<IActionResult> ManualPing(int id)
    {
        using var conn = _registry.CreateConnection();
        var api = await conn.QueryFirstOrDefaultAsync("SELECT Id, BaseUrl, ApiName FROM ApiRegistry WHERE Id = @id", new { id });
        
        if (api == null) return NotFound();

        // Use the existing pinger service to perform an immediate check
        await _pinger.PerformPingAsync((int)api.Id, (string)api.BaseUrl, (string)api.ApiName);
        
        return Ok(new { message = "Ping triggered" });
    }

    // 2. GLOBAL TIMEZONE: Updates every API in the registry
    [HttpPatch("settings/global/timezone")]
    public async Task<IActionResult> UpdateGlobalTimezone([FromBody] JsonElement data)
    {
        string tz = data.GetProperty("timezone").GetString() ?? "UTC";
        using var conn = _registry.CreateConnection();
        
        await conn.ExecuteAsync("UPDATE ApiSettings SET TimeZone = @tz", new { tz });
        
        return Ok(new { message = $"All APIs updated to {tz}" });
    }
    [HttpPost("collect")]
    public async Task<IActionResult> Collect([FromBody] JsonElement report)
    {
        try 
        {
            JsonElement GetProp(JsonElement el, string name) {
                foreach (var p in el.EnumerateObject()) 
                    if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p.Value;
                throw new KeyNotFoundException(name);
            }

            var ownership = GetProp(report, "ownership");
            var systemName = GetProp(ownership, "SystemName").GetString() ?? "Unknown";
            var apiName = GetProp(ownership, "APIName").GetString() ?? "Unknown";
            
            string baseUrl = "";
            if (report.TryGetProperty("BaseUrl", out var b1)) baseUrl = b1.GetString() ?? "";
            else if (report.TryGetProperty("baseUrl", out var b2)) baseUrl = b2.GetString() ?? "";

            string safeSystem = SanitizePath(systemName);
            string safeApi = SanitizePath(apiName);

            var apiDir = Path.Combine(_registry.StoragePath, safeSystem, safeApi);
            if (!Directory.Exists(apiDir)) Directory.CreateDirectory(apiDir);
            
            var swagger = GetProp(report, "swagger");
            await System.IO.File.WriteAllTextAsync(Path.Combine(apiDir, "swagger.json"), swagger.GetRawText(), Encoding.UTF8);

            using var conn = _registry.CreateConnection();
            
            var sql = @"
                INSERT INTO ApiRegistry (SystemName, ApiName, BaseUrl, OwnershipJson, PackagesJson, CodeMapJson, LastUpdated)
                VALUES (@SystemName, @ApiName, @BaseUrl, @Ownership, @Packages, @CodeMap, @LastUpdated)
                ON CONFLICT(SystemName, ApiName) DO UPDATE SET
                    BaseUrl = excluded.BaseUrl,
                    OwnershipJson = excluded.OwnershipJson,
                    PackagesJson = excluded.PackagesJson,
                    CodeMapJson = excluded.CodeMapJson,
                    LastUpdated = excluded.LastUpdated";

            await conn.ExecuteAsync(sql, new {
                SystemName = systemName,
                ApiName = apiName,
                BaseUrl = baseUrl,
                Ownership = ownership.GetRawText(),
                Packages = GetProp(report, "packages").GetRawText(),
                CodeMap = GetProp(report, "codeMap").GetRawText(),
                LastUpdated = DateTime.Now
            });

            var apiId = await conn.ExecuteScalarAsync<int>("SELECT Id FROM ApiRegistry WHERE SystemName=@SystemName AND ApiName=@ApiName", new { SystemName = systemName, ApiName = apiName });
            await conn.ExecuteAsync("INSERT OR IGNORE INTO ApiSettings (ApiId) VALUES (@apiId)", new { apiId });

            // TRIGGER IMMEDIATE PING: User gets "Green Light" immediately on intake
            if (!string.IsNullOrEmpty(baseUrl))
            {
                _ = _pinger.PerformPingAsync(apiId, baseUrl, apiName);
            }

            return Ok(new { message = "Sync Successful" });
        }
        catch (Exception ex) { return Problem(ex.Message); }
    }

    [HttpGet("tree")]
    public async Task<IActionResult> GetTree()
    {
        using var conn = _registry.CreateConnection();
        
        // This query finds the latest status and the timestamp of the start of the current "streak"
        // (i.e., how long it has been in its current state)
        var items = await conn.QueryAsync(@"
            SELECT r.Id, r.SystemName, r.ApiName, 
            (SELECT Status FROM HealthLogs WHERE ApiId = r.Id ORDER BY Timestamp DESC LIMIT 1) as LastStatus,
            (SELECT Timestamp FROM HealthLogs WHERE ApiId = r.Id ORDER BY Timestamp DESC LIMIT 1) as LastCheck,
            (SELECT MIN(Timestamp) FROM (
                SELECT Timestamp, Status FROM HealthLogs 
                WHERE ApiId = r.Id 
                ORDER BY Timestamp DESC 
                LIMIT 10 -- Look back at recent history to find when this state started
            ) WHERE Status = (SELECT Status FROM HealthLogs WHERE ApiId = r.Id ORDER BY Timestamp DESC LIMIT 1)) as StateStarted
            FROM ApiRegistry r");
        
        var tree = items.GroupBy(x => (string)x.SystemName)
                    .Select(g => new { 
                        system = g.Key, 
                        apis = g.Select(x => new { 
                            id = x.Id, 
                            name = x.ApiName, 
                            isHealthy = (x.LastStatus == "Healthy"),
                            lastCheck = x.LastCheck,
                            stateStarted = x.StateStarted, // Used for sorting oldest failure
                            safeSystem = SanitizePath(x.SystemName), 
                            safeApi = SanitizePath(x.ApiName) 
                        }) 
                    });
        return Ok(tree);
    }
    [HttpGet("details/{system}/{api}")]
    public async Task<IActionResult> GetDetails(string system, string api)
    {
        string decodedSystem = WebUtility.UrlDecode(system);
        string decodedApi = WebUtility.UrlDecode(api);
        using var conn = _registry.CreateConnection();
        var result = await conn.QueryFirstOrDefaultAsync("SELECT * FROM ApiRegistry WHERE SystemName = @system AND ApiName = @api", new { system = decodedSystem, api = decodedApi });
        return result != null ? Ok(result) : NotFound();
    }

    [HttpGet("settings/{id:int}")]
    public async Task<IActionResult> GetApiSettings(int id)
    {
        using var conn = _registry.CreateConnection();
        var settings = await conn.QueryFirstOrDefaultAsync("SELECT * FROM ApiSettings WHERE ApiId = @id", new { id });
        return Ok(settings ?? new { ApiId = id, PingIntervalMinutes = 5, TimeZone = "UTC", LogRetentionDays = 30 });
    }

    [HttpPatch("settings/{id:int}")]
    public async Task<IActionResult> UpdateApiSettings(int id, [FromBody] JsonElement settings)
    {
        using var conn = _registry.CreateConnection();
        await conn.ExecuteAsync(@"
            INSERT INTO ApiSettings (ApiId, PingIntervalMinutes, TimeZone, LogRetentionDays)
            VALUES (@id, @ping, @tz, @retain)
            ON CONFLICT(ApiId) DO UPDATE SET
                PingIntervalMinutes = excluded.PingIntervalMinutes,
                TimeZone = excluded.TimeZone,
                LogRetentionDays = excluded.LogRetentionDays", 
            new { id, ping = settings.GetProperty("PingIntervalMinutes").GetInt32(), tz = settings.GetProperty("TimeZone").GetString(), retain = settings.GetProperty("LogRetentionDays").GetInt32() });
        return Ok();
    }

    [HttpGet("health/history/{id:int}")]
    public async Task<IActionResult> GetHealthHistory(int id)
    {
        using var conn = _registry.CreateConnection();
        
        var logs = (await conn.QueryAsync(@"
            SELECT Timestamp, Status, ResponseTimeMs, Details 
            FROM HealthLogs 
            WHERE ApiId = @id 
            ORDER BY Timestamp DESC LIMIT 100", new { id })).ToList();

        if (!logs.Any()) return Ok(new { logs, uptimeLabel = "No Data", lastIncidentLabel = "None", lastIncidentEnd = (DateTime?)null });

        string uptimeLabel = "Initializing...";
        string lastIncidentLabel = "None recorded";
        DateTime? lastIncidentEnd = null;

        var lastFail = logs.FirstOrDefault(l => l.Status != "Healthy");
        
        if (lastFail == null)
        {
            uptimeLabel = "100% Stability";
        }
        else
        {
            var recoveryLog = logs
                .Where(l => l.Status == "Healthy" && DateTime.Parse(l.Timestamp) > DateTime.Parse(lastFail.Timestamp))
                .OrderBy(l => l.Timestamp)
                .FirstOrDefault();

            if (recoveryLog != null)
            {
                var uptimeSpan = DateTime.UtcNow - DateTime.Parse(recoveryLog.Timestamp);
                uptimeLabel = FormatTimeSpan(uptimeSpan) + " Uptime";
                
                var incidentStart = logs
                    .Where(l => l.Status != "Healthy" && DateTime.Parse(l.Timestamp) < DateTime.Parse(recoveryLog.Timestamp))
                    .OrderByDescending(l => l.Timestamp)
                    .LastOrDefault();

                if (incidentStart != null)
                {
                    var duration = DateTime.Parse(recoveryLog.Timestamp) - DateTime.Parse(incidentStart.Timestamp);
                    lastIncidentLabel = FormatTimeSpan(duration);
                    // Pass the raw UTC timestamp to the UI
                    lastIncidentEnd = DateTime.Parse(recoveryLog.Timestamp);
                }
            }
            else
            {
                uptimeLabel = "Offline";
                var downSince = DateTime.UtcNow - DateTime.Parse(lastFail.Timestamp);
                lastIncidentLabel = "Ongoing: " + FormatTimeSpan(downSince);
            }
        }

        return Ok(new { logs, uptimeLabel, lastIncidentLabel, lastIncidentEnd });
    }
    // Helper to turn TimeSpans into "2d 4h 15m" strings
    private string FormatTimeSpan(TimeSpan ts)
    {
        if (ts.TotalDays >= 1) return $"{(int)ts.TotalDays}d {(int)ts.Hours}h";
        if (ts.TotalHours >= 1) return $"{(int)ts.TotalHours}h {(int)ts.Minutes}m";
        return $"{(int)ts.TotalMinutes}m {(int)ts.Seconds}s";
    }

    [HttpDelete("remove/{id:int}")]
    public async Task<IActionResult> RemoveApi(int id)
    {
        using var conn = _registry.CreateConnection();
        var meta = await conn.QueryFirstOrDefaultAsync("SELECT SystemName, ApiName FROM ApiRegistry WHERE Id = @id", new { id });
        if (meta == null) return NotFound();
        var apiDir = Path.Combine(_registry.StoragePath, SanitizePath(meta.SystemName), SanitizePath(meta.ApiName));
        if (Directory.Exists(apiDir)) Directory.Delete(apiDir, true);
        await conn.ExecuteAsync("DELETE FROM ApiRegistry WHERE Id = @id", new { id });
        return Ok(new { message = "Removed" });
    }

    private string SanitizePath(string input)
    {
        if (string.IsNullOrEmpty(input)) return "Unknown";
        string output = input.Replace("クリアエーピーアイ", "Kuriaepiai");
        foreach (char c in Path.GetInvalidFileNameChars()) output = output.Replace(c, '_');
        return output.Trim();
    }
}