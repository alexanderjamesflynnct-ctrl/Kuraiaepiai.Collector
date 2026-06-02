using Dapper;
using Kuraiaepiai.Collector.Data;
using System.Diagnostics;

namespace Kuraiaepiai.Collector.Services;

public interface IHealthMonitor
{
    Task PerformPingAsync(int apiId, string baseUrl, string apiName);
}

public class HealthMonitorService : BackgroundService, IHealthMonitor
{
    private readonly IServiceProvider _services;
    private readonly ILogger<HealthMonitorService> _logger;

    public HealthMonitorService(IServiceProvider services, ILogger<HealthMonitorService> logger)
    {
        _services = services;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Kuriāēpīai Heartbeat Scheduler Started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _services.CreateScope();
                var registry = scope.ServiceProvider.GetRequiredService<CollectorRegistry>();
                using var conn = registry.CreateConnection();

                // DYNAMIC SCHEDULING LOGIC:
                // 1. If never pinged -> Due now.
                // 2. If last status was NOT 'Healthy' -> Due if 30 seconds have passed.
                // 3. If last status was 'Healthy' -> Due if [PingIntervalMinutes] have passed.
                var dueApis = await conn.QueryAsync(@"
                    SELECT r.Id, r.BaseUrl, r.ApiName, 
                           IFNULL(s.PingIntervalMinutes, 5) as StandardInterval,
                           (SELECT Status FROM HealthLogs WHERE ApiId = r.Id ORDER BY Timestamp DESC LIMIT 1) as LastStatus,
                           (SELECT Timestamp FROM HealthLogs WHERE ApiId = r.Id ORDER BY Timestamp DESC LIMIT 1) as LastCheck
                    FROM ApiRegistry r 
                    LEFT JOIN ApiSettings s ON r.Id = s.ApiId
                    WHERE r.BaseUrl IS NOT NULL AND r.BaseUrl != ''
                    AND (
                        r.Id NOT IN (SELECT ApiId FROM HealthLogs) -- Never checked
                        OR 
                        (LastStatus != 'Healthy' AND (strftime('%s','now') - strftime('%s', LastCheck)) >= 30) -- Recovery Check (30s)
                        OR
                        (LastStatus = 'Healthy' AND (strftime('%s','now') - strftime('%s', LastCheck)) / 60 >= IFNULL(s.PingIntervalMinutes, 5)) -- Standard Check
                    )");

                foreach (var api in dueApis)
                {
                    // Execute pings in parallel tasks
                    _ = PerformPingAsync((int)api.Id, (string)api.BaseUrl, (string)api.ApiName);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Scheduler Loop Error: {ex.Message}");
            }

            // The loop now checks the schedule every 30 seconds to support the recovery frequency
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }

    public async Task PerformPingAsync(int apiId, string baseUrl, string apiName)
    {
        if (string.IsNullOrEmpty(baseUrl)) return;

        using var scope = _services.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<CollectorRegistry>();

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        var watch = Stopwatch.StartNew();
        
        try 
        {
            var response = await client.GetAsync($"{baseUrl}/Kuria/health");
            watch.Stop();

            string status = response.IsSuccessStatusCode ? "Healthy" : "Unhealthy";
            string details = response.IsSuccessStatusCode ? "Ping successful" : $"HTTP {(int)response.StatusCode}";

            await SaveLog(apiId, status, (int)watch.ElapsedMilliseconds, details, registry);
            
            // Console feedback for demo
            if (status == "Healthy")
                Console.WriteLine($"[❤] Ping {apiName}: {status} ({watch.ElapsedMilliseconds}ms)");
            else
                Console.WriteLine($"[!] Ping {apiName}: UNHEALTHY (Retrying in 30s)");
        }
        catch (Exception ex) 
        {
            await SaveLog(apiId, "Down", 0, ex.Message, registry);
            Console.WriteLine($"[X] Ping {apiName}: DOWN (Retrying in 30s) - {ex.Message}");
        }
    }

    private async Task SaveLog(int apiId, string status, int ms, string details, CollectorRegistry registry)
    {
        using var conn = registry.CreateConnection();
        
        // 1. Log current result
        await conn.ExecuteAsync(@"
            INSERT INTO HealthLogs (ApiId, Timestamp, Status, ResponseTimeMs, Details) 
            VALUES (@apiId, @ts, @status, @ms, @details)", 
            new { apiId, ts = DateTime.UtcNow, status, ms, details });

        // 2. Cleanup old logs based on retention settings
        await conn.ExecuteAsync(@"
            DELETE FROM HealthLogs 
            WHERE ApiId = @apiId 
            AND Timestamp < datetime('now', '-' || (SELECT IFNULL(LogRetentionDays, 30) FROM ApiSettings WHERE ApiId = @apiId) || ' days')", 
            new { apiId });
    }
}