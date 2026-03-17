using System.Net;
using System.Net.Http.Json;
using Discord_Bot_AI.Configuration;
using Discord_Bot_AI.Infrastructure;
using Discord_Bot_AI.Models;
using Serilog;

namespace Discord_Bot_AI.Services.Agent;

/// <summary>
/// HTTP client for communicating with the OpenClaw agent container.
/// Files are transferred as base64 over HTTP — no shared Docker volume required.
/// </summary>
public class OpenClawAgentClient : IAgentClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _baseUrl;
    private readonly SemaphoreSlim _rateLimiter = new(1, 1);
    private DateTime _lastRequestTime = DateTime.MinValue;
    private const int MinRequestIntervalMs = 500;

    public OpenClawAgentClient(IHttpClientFactory httpClientFactory, AppSettings settings)
    {
        _httpClientFactory = httpClientFactory;
        _baseUrl = settings.OpenClawBaseUrl?.TrimEnd('/') ?? "http://openclaw:8080";
    }

    public async Task<string> SubmitTaskAsync(AgentApiRequest request, CancellationToken cancellationToken = default)
    {
        await ThrottleAsync(cancellationToken);
        using var client = _httpClientFactory.CreateClient(HttpClientNames.OpenClawApi);

        try
        {
            var response = await client.PostAsJsonAsync($"{_baseUrl}/tasks", request, cancellationToken);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                Log.Warning("Agent API rate limited (429).");
                throw new HttpRequestException("Agent rate limited", null, HttpStatusCode.TooManyRequests);
            }

            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<AgentApiResponse>(cancellationToken: cancellationToken);
            return result?.TaskId ?? request.TaskId;
        }
        catch (OperationCanceledException) { throw; }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests) { throw; }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to submit task {TaskId} to agent", request.TaskId);
            throw;
        }
    }

    /// <summary>
    /// Polls GET /tasks/{taskId} for status. Returns null while running/queued.
    /// </summary>
    public async Task<AgentApiResponse?> GetTaskStatusAsync(string taskId, CancellationToken cancellationToken = default)
    {
        await ThrottleAsync(cancellationToken);
        using var client = _httpClientFactory.CreateClient(HttpClientNames.OpenClawApi);

        try
        {
            var response = await client.GetAsync($"{_baseUrl}/tasks/{taskId}", cancellationToken);

            if (response.StatusCode == HttpStatusCode.NotFound)
                return null;

            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<AgentApiResponse>(cancellationToken: cancellationToken);

            if (result?.Status is "running" or "queued")
                return null;

            return result;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to check status of agent task {TaskId}", taskId);
            return null;
        }
    }

    /// <summary>
    /// Downloads file contents for a completed task via GET /tasks/{taskId}/files.
    /// Returns files with base64 content — no filesystem access needed.
    /// </summary>
    public async Task<AgentFilesResponse?> GetTaskFilesAsync(string taskId, CancellationToken cancellationToken = default)
    {
        await ThrottleAsync(cancellationToken);
        using var client = _httpClientFactory.CreateClient(HttpClientNames.OpenClawApi);

        try
        {
            var response = await client.GetAsync($"{_baseUrl}/tasks/{taskId}/files", cancellationToken);

            if (response.StatusCode == HttpStatusCode.NotFound)
                return null;

            response.EnsureSuccessStatusCode();

            return await response.Content.ReadFromJsonAsync<AgentFilesResponse>(cancellationToken: cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to fetch files for agent task {TaskId}", taskId);
            return null;
        }
    }

    public async Task CancelTaskAsync(string taskId, CancellationToken cancellationToken = default)
    {
        using var client = _httpClientFactory.CreateClient(HttpClientNames.OpenClawApi);
        try
        {
            await client.DeleteAsync($"{_baseUrl}/tasks/{taskId}", cancellationToken);
            Log.Information("Sent cancel request for agent task {TaskId}", taskId);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to cancel agent task {TaskId}", taskId);
        }
    }

    private async Task ThrottleAsync(CancellationToken cancellationToken)
    {
        await _rateLimiter.WaitAsync(cancellationToken);
        try
        {
            var elapsed = DateTime.UtcNow - _lastRequestTime;
            if (elapsed.TotalMilliseconds < MinRequestIntervalMs)
                await Task.Delay(MinRequestIntervalMs - (int)elapsed.TotalMilliseconds, cancellationToken);
            _lastRequestTime = DateTime.UtcNow;
        }
        finally
        {
            _rateLimiter.Release();
        }
    }
}