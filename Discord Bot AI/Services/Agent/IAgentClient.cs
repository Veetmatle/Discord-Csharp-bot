using Discord_Bot_AI.Models;

namespace Discord_Bot_AI.Services.Agent;

/// <summary>
/// Abstracts communication with the external AI agent container (OpenClaw).
/// Files are transferred via HTTP as base64 — no shared Docker volume required.
/// </summary>
public interface IAgentClient
{
    Task<string> SubmitTaskAsync(AgentApiRequest request, CancellationToken cancellationToken = default);
    Task<AgentApiResponse?> GetTaskStatusAsync(string taskId, CancellationToken cancellationToken = default);
    Task<AgentFilesResponse?> GetTaskFilesAsync(string taskId, CancellationToken cancellationToken = default);

    Task CancelTaskAsync(string taskId, CancellationToken cancellationToken = default);
}