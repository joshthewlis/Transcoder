using Microsoft.EntityFrameworkCore;
using Transcoder.Server.Data;
using Transcoder.Server.Middleware;
using Transcoder.Server.Options;
using Transcoder.Server.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<TranscoderServerOptions>(builder.Configuration.GetSection("Transcoder"));
builder.Services.Configure<SecurityOptions>(builder.Configuration.GetSection("Transcoder:Security"));
builder.Services.Configure<WorkerTimingOptions>(builder.Configuration.GetSection("Transcoder:WorkerTiming"));
builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection("Transcoder:Storage"));
builder.Services.Configure<ActiveHoursOptions>(builder.Configuration.GetSection("Transcoder:ActiveHours"));
builder.Services.Configure<WorkerRequirementOptions>(builder.Configuration.GetSection("Transcoder:WorkerRequirements"));

var connectionString = builder.Configuration.GetConnectionString("Transcoder") ?? "Data Source=transcoder.db";
builder.Services.AddDbContext<TranscoderDbContext>(options => options.UseSqlite(connectionString));

builder.Services.AddControllers().AddJsonOptions(options =>
{
    options.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
    options.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
});
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHttpClient();

builder.Services.AddSingleton<ScanQueue>();
builder.Services.AddSingleton<LibraryWatcherState>();
builder.Services.AddScoped<SystemSettingsService>();
builder.Services.AddScoped<PathCheckDefinitionService>();
builder.Services.AddScoped<JobLeaseService>();
builder.Services.AddScoped<JobCompletionService>();
builder.Services.AddScoped<ReplacementService>();
builder.Services.AddScoped<TranscodePlanService>();
builder.Services.AddSingleton<IntegrationApiClient>();
builder.Services.AddScoped<MetadataRefreshService>();
builder.Services.AddScoped<StorageMapImportService>();
builder.Services.AddScoped<LibraryScanner>();
builder.Services.AddScoped<StorageInitializer>();
builder.Services.AddHostedService<LibraryScanBackgroundService>();
builder.Services.AddHostedService<LibraryWatcherBackgroundService>();
builder.Services.AddHostedService<WorkerMonitorService>();
builder.Services.AddHostedService<AutoReplaceBackgroundService>();
builder.Services.AddHostedService<AutoQueueBackgroundService>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<TranscoderDbContext>();
    db.Database.EnsureCreated();
    ProfileSeeder.SeedDefaultsAsync(db).GetAwaiter().GetResult();
    scope.ServiceProvider.GetRequiredService<StorageInitializer>().EnsureStorageFolders();
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseDefaultFiles();
app.UseStaticFiles();

app.UseMiddleware<ApiKeyMiddleware>();
app.MapControllers();
app.MapGet("/", () => Results.Redirect("/index.html"));

app.Run();
