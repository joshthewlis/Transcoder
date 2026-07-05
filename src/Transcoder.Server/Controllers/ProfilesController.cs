using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Transcoder.Contracts;
using Transcoder.Server.Data;
using Transcoder.Server.Data.Entities;

namespace Transcoder.Server.Controllers;

[ApiController]
[Route("api/profiles")]
public sealed class ProfilesController(TranscoderDbContext db) : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    [HttpGet]
    public async Task<ActionResult<List<TranscodeProfileDto>>> List(CancellationToken cancellationToken)
    {
        var profiles = await db.Profiles.AsNoTracking().OrderBy(x => x.Name).ToListAsync(cancellationToken);
        return profiles.Select(ToDto).ToList();
    }

    [HttpGet("{name}")]
    public async Task<ActionResult<TranscodeProfileDto>> Get(string name, CancellationToken cancellationToken)
    {
        var profile = await db.Profiles.AsNoTracking().FirstOrDefaultAsync(x => x.Name == name, cancellationToken);
        return profile is null ? NotFound() : ToDto(profile);
    }

    [HttpPost]
    public async Task<ActionResult<TranscodeProfileDto>> Create(CreateProfileRequest request, CancellationToken cancellationToken)
    {
        var entity = new ProfileEntity
        {
            Name = request.Name,
            TargetCodec = request.TargetCodec,
            Encoder = request.Encoder,
            ArgumentsJson = JsonSerializer.Serialize(request.Arguments, JsonOptions),
            RequiredEncodersJson = JsonSerializer.Serialize(request.RequiredEncoders, JsonOptions),
            Enabled = request.Enabled
        };
        db.Profiles.Add(entity);
        await db.SaveChangesAsync(cancellationToken);
        return CreatedAtAction(nameof(Get), new { name = entity.Name }, ToDto(entity));
    }

    [HttpPut("{name}")]
    public async Task<IActionResult> Update(string name, UpdateProfileRequest request, CancellationToken cancellationToken)
    {
        var entity = await db.Profiles.FirstOrDefaultAsync(x => x.Name == name, cancellationToken);
        if (entity is null) return NotFound();
        entity.Name = request.Name;
        entity.TargetCodec = request.TargetCodec;
        entity.Encoder = request.Encoder;
        entity.ArgumentsJson = JsonSerializer.Serialize(request.Arguments, JsonOptions);
        entity.RequiredEncodersJson = JsonSerializer.Serialize(request.RequiredEncoders, JsonOptions);
        entity.Enabled = request.Enabled;
        await db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    [HttpDelete("{name}")]
    public async Task<IActionResult> Delete(string name, CancellationToken cancellationToken)
    {
        var entity = await db.Profiles.FirstOrDefaultAsync(x => x.Name == name, cancellationToken);
        if (entity is null) return NotFound();
        db.Profiles.Remove(entity);
        await db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    private static TranscodeProfileDto ToDto(ProfileEntity entity) => new()
    {
        Id = entity.Id,
        Name = entity.Name,
        TargetCodec = entity.TargetCodec,
        Encoder = entity.Encoder,
        Arguments = DeserializeList(entity.ArgumentsJson),
        RequiredEncoders = DeserializeList(entity.RequiredEncodersJson),
        Enabled = entity.Enabled
    };

    private static List<string> DeserializeList(string json)
    {
        try { return JsonSerializer.Deserialize<List<string>>(json, JsonOptions) ?? []; }
        catch { return []; }
    }
}
