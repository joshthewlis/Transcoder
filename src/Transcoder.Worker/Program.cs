using Transcoder.Worker.Configuration;
using Transcoder.Worker.Services;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.Configure<WorkerOptions>(builder.Configuration.GetSection("Transcoder"));
builder.Services.AddHttpClient<WorkerApiClient>();
builder.Services.AddSingleton<PathMapper>();
builder.Services.AddSingleton<CapabilityDetector>();
builder.Services.AddSingleton<FfprobeRunner>();
builder.Services.AddSingleton<FfmpegRunner>();
builder.Services.AddSingleton<ShutdownService>();
builder.Services.AddHostedService<WorkerLoopService>();

var host = builder.Build();
host.Run();
