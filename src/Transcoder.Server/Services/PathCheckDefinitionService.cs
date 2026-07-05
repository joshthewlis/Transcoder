using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Transcoder.Contracts;
using Transcoder.Server.Data;
using Transcoder.Server.Options;

namespace Transcoder.Server.Services;

public sealed class PathCheckDefinitionService(TranscoderDbContext db, IOptions<StorageOptions> storageOptions)
{
    public async Task<List<PathCheckDefinitionDto>> BuildPathChecksAsync(CancellationToken cancellationToken = default)
    {
        var storage = storageOptions.Value;
        var checks = new List<PathCheckDefinitionDto>
        {
            new()
            {
                Id = "transcoder-root",
                ServerPath = storage.TranscoderRoot,
                MustExist = true,
                MustBeReadable = true,
                MustBeWritable = true,
                AllowCreateIfMissing = storage.AutoCreateStorageFolders
            },
            new()
            {
                Id = "transcoder-staging",
                ServerPath = storage.StagingRoot,
                MustExist = true,
                MustBeReadable = true,
                MustBeWritable = true,
                AllowCreateIfMissing = storage.AutoCreateStorageFolders
            }
        };

        var libraries = await db.Libraries.AsNoTracking()
            .Where(x => x.Enabled)
            .OrderBy(x => x.Id)
            .ToListAsync(cancellationToken);

        checks.AddRange(libraries.Select(library => new PathCheckDefinitionDto
        {
            Id = $"library-{library.Id}-root",
            LibraryId = library.Id,
            ServerPath = library.RootPath,
            MustExist = true,
            MustBeReadable = true,
            MustBeWritable = false
        }));

        return checks;
    }
}
