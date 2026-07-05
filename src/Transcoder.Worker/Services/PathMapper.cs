using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Transcoder.Contracts;
using Transcoder.Worker.Configuration;

namespace Transcoder.Worker.Services;

public sealed class PathMapper
{
    private readonly WorkerOptions _options;
    private readonly ILogger<PathMapper> _logger;
    private readonly object _sync = new();
    private List<PathMappingOptions> _mappings = [];

    public PathMapper(IOptions<WorkerOptions> options, ILogger<PathMapper> logger)
    {
        _options = options.Value;
        _logger = logger;
        ReloadFromConfiguredAndRuntimeFile();
    }

    public IReadOnlyList<string> ServerPrefixes
    {
        get
        {
            lock (_sync)
            {
                return _mappings.Select(x => x.ServerPrefix).ToList();
            }
        }
    }

    public string MappingConfigHash
    {
        get
        {
            lock (_sync)
            {
                var text = string.Join("|", _mappings.Select(x => $"{x.ServerPrefix}=>{x.WorkerPrefix}"));
                return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
            }
        }
    }

    public bool TryMap(string serverPath, out string workerPath)
    {
        List<PathMappingOptions> snapshot;
        lock (_sync)
        {
            snapshot = _mappings.ToList();
        }

        foreach (var mapping in snapshot)
        {
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!serverPath.StartsWith(mapping.ServerPrefix, comparison))
                continue;

            var suffix = serverPath[mapping.ServerPrefix.Length..].TrimStart('/', '\\');
            workerPath = Path.Combine(mapping.WorkerPrefix, suffix.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar));
            return true;
        }

        workerPath = serverPath;
        return File.Exists(workerPath) || Directory.Exists(workerPath);
    }

    public bool ApplyRuntimeSettings(WorkerRuntimeSettingsDto settings)
    {
        var runtimeMappings = settings.PathMappings
            .Where(x => !string.IsNullOrWhiteSpace(x.ServerPrefix) && !string.IsNullOrWhiteSpace(x.WorkerPrefix))
            .Select(x => new PathMappingOptions
            {
                ServerPrefix = x.ServerPrefix.TrimEnd('/', '\\'),
                WorkerPrefix = x.WorkerPrefix.TrimEnd('/', '\\')
            })
            .ToList();

        var before = MappingConfigHash;
        SaveRuntimeMappings(runtimeMappings);
        ReloadFromConfiguredAndRuntimeFile();
        var changed = !string.Equals(before, MappingConfigHash, StringComparison.OrdinalIgnoreCase);
        if (changed)
            _logger.LogInformation("Worker path mappings changed. New mapping hash {Hash}", MappingConfigHash);
        return changed;
    }

    private void ReloadFromConfiguredAndRuntimeFile()
    {
        var configured = _options.PathMappings
            .Where(x => !string.IsNullOrWhiteSpace(x.ServerPrefix) && !string.IsNullOrWhiteSpace(x.WorkerPrefix))
            .Select(x => new PathMappingOptions
            {
                ServerPrefix = x.ServerPrefix.TrimEnd('/', '\\'),
                WorkerPrefix = x.WorkerPrefix.TrimEnd('/', '\\')
            })
            .ToList();

        var runtime = LoadRuntimeMappings();
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var merged = configured
            .Concat(runtime)
            .GroupBy(x => x.ServerPrefix, comparer)
            .Select(g => g.Last())
            .OrderByDescending(x => x.ServerPrefix.Length)
            .ToList();

        lock (_sync)
        {
            _mappings = merged;
        }
    }

    private List<PathMappingOptions> LoadRuntimeMappings()
    {
        try
        {
            var file = RuntimeSettingsPath();
            if (!File.Exists(file)) return [];
            var settings = JsonSerializer.Deserialize<WorkerRuntimeSettingsDto>(File.ReadAllText(file), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            return settings?.PathMappings.Select(x => new PathMappingOptions
            {
                ServerPrefix = x.ServerPrefix,
                WorkerPrefix = x.WorkerPrefix
            }).ToList() ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read worker runtime settings file.");
            return [];
        }
    }

    private void SaveRuntimeMappings(List<PathMappingOptions> mappings)
    {
        try
        {
            Directory.CreateDirectory(_options.LocalWorkingRoot);
            var settings = new WorkerRuntimeSettingsDto
            {
                UpdatedUtc = DateTime.UtcNow,
                PathMappings = mappings.Select(x => new WorkerPathMappingDto
                {
                    ServerPrefix = x.ServerPrefix,
                    WorkerPrefix = x.WorkerPrefix
                }).ToList()
            };
            File.WriteAllText(RuntimeSettingsPath(), JsonSerializer.Serialize(settings, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not write worker runtime settings file.");
        }
    }

    private string RuntimeSettingsPath() => Path.Combine(_options.LocalWorkingRoot, "worker-runtime-settings.json");
}
