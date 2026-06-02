using Kuraiaepiai.Collector.Data;
using Kuraiaepiai.Collector.Services;
using Microsoft.Extensions.FileProviders;
using Microsoft.AspNetCore.OpenApi;

var builder = WebApplication.CreateBuilder(args);

// ── 1. SERVICES ──────────────────────────────────────────────────────────────
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddOpenApi(); 
builder.Services.AddSwaggerGen(); 

builder.Services.AddCors(options => {
    options.AddPolicy("AllowKuraiaepiaiUI", policy => {
        policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod();
    });
});

// Database Registry
builder.Services.AddSingleton<CollectorRegistry>();

// Health Monitoring Service (Registered as both Singleton and Background Service)
builder.Services.AddSingleton<HealthMonitorService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<HealthMonitorService>());
builder.Services.AddSingleton<IHealthMonitor>(sp => sp.GetRequiredService<HealthMonitorService>());

var app = builder.Build();

// ── 2. INITIALIZATION ────────────────────────────────────────────────────────
app.Services.GetRequiredService<CollectorRegistry>().Initialize();

// ── 3. PIPELINE ──────────────────────────────────────────────────────────────
app.UseCors("AllowKuraiaepiaiUI");

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseSwaggerUI(c => {
        c.SwaggerEndpoint("/openapi/v1.json", "clearAPI Collector v1");
        c.RoutePrefix = "swagger";
    });
}

app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(app.Services.GetRequiredService<CollectorRegistry>().StoragePath),
    RequestPath = "/docs"
});

app.UseHttpsRedirection();
app.MapControllers(); 

Console.WriteLine("クリアエーピーアイ (Kuriāēpīai) Collector running on port 8000...");
app.Run("http://localhost:8000");