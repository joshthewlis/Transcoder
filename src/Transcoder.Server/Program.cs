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
builder.Services.AddScoped<ProcessingFactsRepairService>();
builder.Services.AddHostedService<LibraryScanBackgroundService>();
builder.Services.AddHostedService<LibraryWatcherBackgroundService>();
builder.Services.AddHostedService<WorkerMonitorService>();
builder.Services.AddHostedService<AutoReplaceBackgroundService>();
builder.Services.AddHostedService<AutoQueueBackgroundService>();
builder.Services.AddHostedService<StorageMapAutoImportService>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<TranscoderDbContext>();
    db.Database.EnsureCreated();

    // EnsureCreated() does not add new tables to an existing SQLite database.
    // Keep this lightweight compatibility upgrade until the project moves to EF migrations.
    db.Database.ExecuteSqlRaw(@"
        CREATE TABLE IF NOT EXISTS MediaPlanHistories (
            Id INTEGER NOT NULL CONSTRAINT PK_MediaPlanHistories PRIMARY KEY AUTOINCREMENT,
            MediaItemId INTEGER NOT NULL,
            Revision INTEGER NOT NULL,
            IsCurrent INTEGER NOT NULL,
            PlanJson TEXT NOT NULL,
            PlanHash TEXT NULL,
            PlanKind TEXT NULL,
            ProcessingStrategy TEXT NULL,
            InputFileSizeBytes INTEGER NOT NULL,
            EstimatedRemovedBytes INTEGER NULL,
            EstimatedOutputSizeBytes INTEGER NULL,
            EstimatedSavingsComplete INTEGER NOT NULL,
            CleanupRequired INTEGER NOT NULL,
            PlanReviewJson TEXT NULL,
            PlanCreatedUtc TEXT NOT NULL,
            PlanReviewedUtc TEXT NULL,
            SupersededUtc TEXT NULL,
            CONSTRAINT FK_MediaPlanHistories_MediaItems_MediaItemId
                FOREIGN KEY (MediaItemId) REFERENCES MediaItems (Id) ON DELETE CASCADE
        );
        CREATE UNIQUE INDEX IF NOT EXISTS IX_MediaPlanHistories_MediaItemId_Revision
            ON MediaPlanHistories (MediaItemId, Revision);
        CREATE INDEX IF NOT EXISTS IX_MediaPlanHistories_MediaItemId_IsCurrent
            ON MediaPlanHistories (MediaItemId, IsCurrent);
    " );

    ProfileSeeder.SeedDefaultsAsync(db).GetAwaiter().GetResult();
    scope.ServiceProvider.GetRequiredService<StorageInitializer>().EnsureStorageFolders();

    // Upgrade repair: older Reset/Replan behaviour could clear MetadataJson and therefore
    // hide completed cleanup/transcode savings. Completed job results are immutable enough
    // to restore those stage facts. This is idempotent and only fills missing values.
    scope.ServiceProvider.GetRequiredService<ProcessingFactsRepairService>()
        .RepairAsync()
        .GetAwaiter()
        .GetResult();
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
