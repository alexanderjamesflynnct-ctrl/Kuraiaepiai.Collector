using System.Text;
using System.IO;
using System.Text.Json;
using Swashbuckle.AspNetCore.Swagger;
using Microsoft.OpenApi;
using kuraiaepiai.Source;
using Kuraiaepiai.Collector.Data;
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

builder.Services.AddSingleton<CollectorRegistry>();

var app = builder.Build();

// ── 2. INITIALIZATION ────────────────────────────────────────────────────────
var registry = app.Services.GetRequiredService<CollectorRegistry>();
registry.Initialize();

// ── 3. PIPELINE ──────────────────────────────────────────────────────────────
app.UseCors("AllowKuraiaepiaiUI");

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseSwaggerUI(c => {
        c.SwaggerEndpoint("/openapi/v1.json", "Registry API v1");
        c.RoutePrefix = "swagger";
    });
}

app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(registry.StoragePath),
    RequestPath = "/docs"
});

app.UseHttpsRedirection();

// Maps only the standard Registry routes (Collect, Tree, Details, Remove)
app.MapControllers(); 


// <clearapi-start>
if (app.Environment.IsDevelopment())
{
    app.UseCors("KuraiaepiaiPolicy");
    app.MapGet("/clearapi/push", async (HttpContext context) => {
        try {
            string jsonContent = "";
            var swaggerProvider = context.RequestServices.GetService<ISwaggerProvider>();
            if (swaggerProvider != null) {
                var doc = swaggerProvider.GetSwagger("v1", null, "/");
                doc.Servers = new List<OpenApiServer> { new OpenApiServer { Url = $"{context.Request.Scheme}://{context.Request.Host}" } };
                using var sw = new StringWriter();
                doc.SerializeAsV3(new OpenApiJsonWriter(sw));
                jsonContent = sw.ToString();
            } else {
                using var client = new HttpClient();
                jsonContent = await client.GetStringAsync($"{context.Request.Scheme}://{context.Request.Host}/openapi/v1.json");
            }
            await File.WriteAllTextAsync("swagger.json", jsonContent, Encoding.UTF8);
            var report = await (new KuraiaepiaiReporter()).GenerateReport(Directory.GetCurrentDirectory(), jsonContent);
            using var client2 = new HttpClient();
            var response = await client2.PostAsJsonAsync("http://localhost:8000/api/collect", report);
            return response.IsSuccessStatusCode ? Results.Ok("Synced!") : Results.BadRequest("Sync failed.");
        } catch (Exception ex) { return Results.Problem(ex.Message); }
    });
}
// <clearapi-end>
app.Run("http://localhost:8000");