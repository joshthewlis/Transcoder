using Microsoft.Extensions.Options;
using Transcoder.Server.Options;

namespace Transcoder.Server.Services;

public sealed class StorageInitializer(IOptions<StorageOptions> storageOptions, ILogger<StorageInitializer> logger)
{
    public void EnsureStorageFolders()
    {
        var storage = storageOptions.Value;
        if (!storage.AutoCreateStorageFolders)
        {
            logger.LogInformation("Storage folder auto-creation is disabled.");
            return;
        }

        EnsureDirectoryUnderExistingParent(storage.TranscoderRoot, "transcoder root");
        EnsureDirectoryUnderExistingParent(storage.WorkingRoot, "working root");
        EnsureDirectoryUnderExistingParent(storage.StagingRoot, "staging root");
    }

    private void EnsureDirectoryUnderExistingParent(string path, string name)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            logger.LogWarning("Cannot create {Name}: path is empty.", name);
            return;
        }

        if (Directory.Exists(path))
        {
            logger.LogInformation("Storage {Name} exists: {Path}", name, path);
            return;
        }

        var parent = Directory.GetParent(path);
        if (parent is null || !parent.Exists)
        {
            logger.LogWarning("Cannot create storage {Name} at {Path}: parent directory does not exist.", name, path);
            return;
        }

        Directory.CreateDirectory(path);
        logger.LogInformation("Created storage {Name}: {Path}", name, path);
    }
}
