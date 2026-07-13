using Transcoder.Worker.Configuration;
using Transcoder.Worker.Services;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.Configure<WorkerOptions>(builder.Configuration.GetSection("Transcoder"));
builder.Services.AddHttpClient<WorkerApiClient>(client =>
{
    // Some server callbacks, especially job completion after large SMB/NAS copy operations,
    // can legitimately take longer than HttpClient's default 100 second timeout.
    client.Timeout = Timeout.InfiniteTimeSpan;
});
builder.Services.AddSingleton<PathMapper>();
builder.Services.AddSingleton<CapabilityDetector>();
builder.Services.AddSingleton<FfprobeRunner>();
builder.Services.AddSingleton<FfmpegRunner>();
builder.Services.AddSingleton<ShutdownService>();
builder.Services.AddHostedService<WorkerLoopService>();

var host = builder.Build();
host.Run();
