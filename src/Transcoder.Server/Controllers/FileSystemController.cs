using Microsoft.AspNetCore.Mvc;
using Transcoder.Contracts;

namespace Transcoder.Server.Controllers;

[ApiController]
[Route("api/filesystem")]
public sealed class FileSystemController : ControllerBase
{
    [HttpGet("browse")]
    public ActionResult<List<FileSystemEntryDto>> Browse([FromQuery] string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            path = Path.GetPathRoot(Environment.CurrentDirectory) ?? Environment.CurrentDirectory;

        var fullPath = Path.GetFullPath(path);
        if (!Directory.Exists(fullPath))
            return NotFound(new { path = fullPath, message = "Directory not found." });

        var directory = new DirectoryInfo(fullPath);
        var entries = new List<FileSystemEntryDto>();

        try
        {
            entries.AddRange(directory.EnumerateDirectories().OrderBy(x => x.Name).Select(x => new FileSystemEntryDto
            {
                Name = x.Name,
                FullPath = x.FullName,
                IsDirectory = true,
                LastModifiedUtc = x.LastWriteTimeUtc
            }));

            entries.AddRange(directory.EnumerateFiles().OrderBy(x => x.Name).Select(x => new FileSystemEntryDto
            {
                Name = x.Name,
                FullPath = x.FullName,
                IsDirectory = false,
                SizeBytes = x.Length,
                LastModifiedUtc = x.LastWriteTimeUtc
            }));
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { path = fullPath, error = ex.Message });
        }

        return entries;
    }
}
