using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Transcoder.Contracts;
using Transcoder.Server.Data;
using Transcoder.Server.Data.Entities;
using Transcoder.Server.Services;

namespace Transcoder.Server.Controllers;

[ApiController]
[Route("api/integrations")]
public sealed class IntegrationsController(TranscoderDbContext db, IntegrationApiClient integrationApi) : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [HttpGet]
    public async Task<ActionResult<List<IntegrationDto>>> List(CancellationToken cancellationToken)
    {
        var integrations = await db.Integrations.AsNoTracking().OrderBy(x => x.IntegrationType).ThenBy(x => x.Name).ToListAsync(cancellationToken);
        return integrations.Select(ToDto).ToList();
    }

    [HttpPost]
    public async Task<ActionResult<IntegrationDto>> Create(CreateIntegrationRequest request, CancellationToken cancellationToken)
    {
        var entity = new IntegrationEntity
        {
            Name = request.Name.Trim(),
            IntegrationType = request.IntegrationType,
            Enabled = request.Enabled,
            BaseUrl = NormalizeBaseUrl(request.BaseUrl, request.IntegrationType),
            ApiKey = string.IsNullOrWhiteSpace(request.ApiKey) ? null : request.ApiKey.Trim(),
            PathMappingsJson = JsonSerializer.Serialize(request.PathMappings, JsonOptions),
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };

        db.Integrations.Add(entity);
        await db.SaveChangesAsync(cancellationToken);
        return CreatedAtAction(nameof(Get), new { integrationId = entity.Id }, ToDto(entity));
    }

    [HttpGet("{integrationId:int}")]
    public async Task<ActionResult<IntegrationDto>> Get(int integrationId, CancellationToken cancellationToken)
    {
        var entity = await db.Integrations.AsNoTracking().FirstOrDefaultAsync(x => x.Id == integrationId, cancellationToken);
        return entity is null ? NotFound() : ToDto(entity);
    }

    [HttpPut("{integrationId:int}")]
    public async Task<IActionResult> Update(int integrationId, UpdateIntegrationRequest request, CancellationToken cancellationToken)
    {
        var entity = await db.Integrations.FirstOrDefaultAsync(x => x.Id == integrationId, cancellationToken);
        if (entity is null) return NotFound();

        entity.Name = request.Name.Trim();
        entity.IntegrationType = request.IntegrationType;
        entity.Enabled = request.Enabled;
        entity.BaseUrl = NormalizeBaseUrl(request.BaseUrl, request.IntegrationType);
        if (!string.IsNullOrWhiteSpace(request.ApiKey))
            entity.ApiKey = request.ApiKey.Trim();
        entity.PathMappingsJson = JsonSerializer.Serialize(request.PathMappings, JsonOptions);
        entity.UpdatedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    [HttpDelete("{integrationId:int}")]
    public async Task<IActionResult> Delete(int integrationId, CancellationToken cancellationToken)
    {
        var entity = await db.Integrations.FirstOrDefaultAsync(x => x.Id == integrationId, cancellationToken);
        if (entity is null) return NotFound();
        db.Integrations.Remove(entity);
        await db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    [HttpPost("{integrationId:int}/test")]
    public async Task<ActionResult<TestIntegrationResultDto>> Test(int integrationId, CancellationToken cancellationToken)
    {
        var entity = await db.Integrations.AsNoTracking().FirstOrDefaultAsync(x => x.Id == integrationId, cancellationToken);
        if (entity is null) return NotFound();
        return await integrationApi.TestAsync(entity, cancellationToken);
    }

    [HttpPost("test")]
    public async Task<ActionResult<TestIntegrationResultDto>> TestUnsaved(CreateIntegrationRequest request, CancellationToken cancellationToken)
    {
        var entity = new IntegrationEntity
        {
            Name = string.IsNullOrWhiteSpace(request.Name) ? "Unsaved integration test" : request.Name.Trim(),
            IntegrationType = request.IntegrationType,
            Enabled = request.Enabled,
            BaseUrl = NormalizeBaseUrl(request.BaseUrl, request.IntegrationType),
            ApiKey = string.IsNullOrWhiteSpace(request.ApiKey) ? null : request.ApiKey.Trim(),
            PathMappingsJson = JsonSerializer.Serialize(request.PathMappings, JsonOptions),
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };

        return await integrationApi.TestAsync(entity, cancellationToken);
    }

    private static IntegrationDto ToDto(IntegrationEntity entity) => new()
    {
        Id = entity.Id,
        Name = entity.Name,
        IntegrationType = entity.IntegrationType,
        Enabled = entity.Enabled,
        BaseUrl = entity.BaseUrl,
        HasApiKey = !string.IsNullOrWhiteSpace(entity.ApiKey),
        PathMappings = DeserializeMappings(entity.PathMappingsJson),
        CreatedUtc = entity.CreatedUtc,
        UpdatedUtc = entity.UpdatedUtc
    };

    private static List<IntegrationPathMappingDto> DeserializeMappings(string json)
    {
        try { return JsonSerializer.Deserialize<List<IntegrationPathMappingDto>>(json, JsonOptions) ?? []; }
        catch { return []; }
    }

    private static string NormalizeBaseUrl(string baseUrl, IntegrationType type)
    {
        if (string.IsNullOrWhiteSpace(baseUrl) && type == IntegrationType.Tmdb)
            return "https://api.themoviedb.org";
        return baseUrl.Trim().TrimEnd('/');
    }
}
