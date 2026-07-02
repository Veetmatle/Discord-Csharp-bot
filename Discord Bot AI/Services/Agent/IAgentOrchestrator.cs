using Discord_Bot_AI.Models;

namespace Discord_Bot_AI.Services.Agent;

/// <summary>
/// Manages the task queue and lifecycle of agent tasks.
/// Coordinates submission, polling, timeout enforcement and result delivery.
/// </summary>
public interface IAgentOrchestrator
{
    Task<string> EnqueueTaskAsync(AgentTask task, CancellationToken cancellationToken = default);
    AgentTask? GetTask(string taskId);
    Task StartProcessingAsync(CancellationToken cancellationToken);
    Task StopAsync();

    /// <summary>
    /// Callback invoked when a task completes (success or failure).
    /// Set by BotService to deliver results to Discord.
    /// </summary>
    Func<AgentTaskResult, AgentTask, CancellationToken, Task>? OnTaskCompleted { get; set; }
}
