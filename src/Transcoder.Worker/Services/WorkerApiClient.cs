using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Transcoder.Contracts;
using Transcoder.Worker.Configuration;

namespace Transcoder.Worker.Services;

public sealed class WorkerApiClient(HttpClient httpClient, IOptions<WorkerOptions> options)
{
    private readonly WorkerOptions _options = options.Value;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private HttpClient Client
    {
        get
        {
            httpClient.BaseAddress ??= new Uri(_options.ServerUrl.TrimEnd('/') + "/");
            if (!httpClient.DefaultRequestHeaders.Contains("X-Transcoder-Api-Key"))
                httpClient.DefaultRequestHeaders.Add("X-Transcoder-Api-Key", _options.ApiKey);
            if (!httpClient.DefaultRequestHeaders.Contains("X-Transcoder-Worker-Id"))
                httpClient.DefaultRequestHeaders.Add("X-Transcoder-Worker-Id", _options.WorkerId);
            return httpClient;
        }
    }

    public async Task<WorkerRegisterResponse> RegisterAsync(WorkerRegisterRequest request, CancellationToken cancellationToken)
    {
        Thread.Sleep(5000);
        var response = await Client.PostAsJsonAsync("api/workers/register", request, _jsonOptions, cancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<WorkerRegisterResponse>(_jsonOptions, cancellationToken))!;
    }

    public async Task<WorkerRuntimeSettingsDto?> GetRuntimeSettingsAsync(string workerId, CancellationToken cancellationToken)
    {
        var response = await Client.GetAsync($"api/workers/{workerId}/runtime-settings", cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<WorkerRuntimeSettingsDto>(_jsonOptions, cancellationToken);
    }

    public async Task SendPathChecksAsync(string workerId, WorkerPathCheckResultsRequest request, CancellationToken cancellationToken)
    {
        var response = await Client.PostAsJsonAsync($"api/workers/{workerId}/path-checks", request, _jsonOptions, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<WorkerHeartbeatResponse> HeartbeatAsync(string workerId, WorkerHeartbeatRequest request, CancellationToken cancellationToken)
    {
        var response = await Client.PostAsJsonAsync($"api/workers/{workerId}/heartbeat", request, _jsonOptions, cancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<WorkerHeartbeatResponse>(_jsonOptions, cancellationToken))!;
    }

    public async Task<JobLeaseBatchResponse> LeaseBatchAsync(LeaseBatchRequest request, CancellationToken cancellationToken)
    {
        var response = await Client.PostAsJsonAsync("api/jobs/lease-batch", request, _jsonOptions, cancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JobLeaseBatchResponse>(_jsonOptions, cancellationToken))!;
    }

    public async Task CompleteJobAsync(long jobId, JobCompleteRequest request, CancellationToken cancellationToken)
    {
        var response = await Client.PostAsJsonAsync($"api/jobs/{jobId}/complete", request, _jsonOptions, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task FailJobAsync(long jobId, JobFailRequest request, CancellationToken cancellationToken)
    {
        var response = await Client.PostAsJsonAsync($"api/jobs/{jobId}/fail", request, _jsonOptions, cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}
