using Microsoft.AspNetCore.Mvc;
using Kuraiaepiai.Collector.Data;
using Dapper;
using System.Text.Json;
using System.Text;
using System.Net;

namespace Kuraiaepiai.Collector.Controllers;

[ApiController]
[Route("api")]
[Tags("Registry-Engine")]
public class CollectorController : ControllerBase
{
    private readonly CollectorRegistry _registry;

    public CollectorController(CollectorRegistry registry)
    {
        _registry = registry;
    }

    [HttpPost("collect")]
    public async Task<IActionResult> Collect([FromBody] JsonElement report)
    {
        try {
            JsonElement GetProp(JsonElement el, string name) {
                foreach (var p in el.EnumerateObject()) 
                    if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p.Value;
                throw new KeyNotFoundException(name);
            }

            var ownership = GetProp(report, "ownership");
            var systemName = GetProp(ownership, "SystemName").GetString() ?? "Unknown";
            var apiName = GetProp(ownership, "APIName").GetString() ?? "Unknown";

            string safeSystem = SanitizePath(systemName);
            string safeApi = SanitizePath(apiName);

            var apiDir = Path.Combine(_registry.StoragePath, safeSystem, safeApi);
            if (!Directory.Exists(apiDir)) Directory.CreateDirectory(apiDir);
            
            var swagger = GetProp(report, "swagger");
            await System.IO.File.WriteAllTextAsync(Path.Combine(apiDir, "swagger.json"), swagger.GetRawText(), Encoding.UTF8);

            using var conn = _registry.CreateConnection();
            var sql = @"
                INSERT INTO ApiRegistry (SystemName, ApiName, OwnershipJson, PackagesJson, CodeMapJson, LastUpdated)
                VALUES (@SystemName, @ApiName, @Ownership, @Packages, @CodeMap, @LastUpdated)
                ON CONFLICT(SystemName, ApiName) DO UPDATE SET
                    OwnershipJson = excluded.OwnershipJson,
                    PackagesJson = excluded.PackagesJson,
                    CodeMapJson = excluded.CodeMapJson,
                    LastUpdated = excluded.LastUpdated";

            await conn.ExecuteAsync(sql, new {
                SystemName = systemName,
                ApiName = apiName,
                Ownership = ownership.GetRawText(),
                Packages = GetProp(report, "packages").GetRawText(),
                CodeMap = GetProp(report, "codeMap").GetRawText(),
                LastUpdated = DateTime.Now
            });

            return Ok(new { message = "Sync Successful" });
        } catch (Exception ex) { return Problem(ex.Message); }
    }

    [HttpGet("tree")]
    public async Task<IActionResult> GetTree()
    {
        using var conn = _registry.CreateConnection();
        var items = await conn.QueryAsync("SELECT Id, SystemName, ApiName FROM ApiRegistry");
        var tree = items.GroupBy(x => (string)x.SystemName)
                    .Select(g => new { 
                        system = g.Key, 
                        apis = g.Select(x => new { id = x.Id, name = x.ApiName, safeSystem = SanitizePath(x.SystemName), safeApi = SanitizePath(x.ApiName) }) 
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