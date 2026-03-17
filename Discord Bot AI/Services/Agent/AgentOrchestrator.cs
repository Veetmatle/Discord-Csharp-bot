using System.Collections.Concurrent;
using System.Threading.Channels;
using Discord_Bot_AI.Configuration;
using Discord_Bot_AI.Models;
using Serilog;

namespace Discord_Bot_AI.Services.Agent;

/// <summary>
/// Manages the agent task queue. Files are fetched via HTTP from OpenClaw —
/// no shared Docker volume required.
/// </summary>
public class AgentOrchestrator : IAgentOrchestrator, IDisposable
{
    private readonly IAgentClient _agentClient;
    private readonly AppSettings _settings;
    private readonly Channel<AgentTask> _taskQueue;
    private readonly ConcurrentDictionary<string, AgentTask> _activeTasks = new();
    private CancellationTokenSource? _processingCts;
    private Task? _processingLoop;
    private bool _disposed;

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);
    private const int MaxQueueCapacity = 20;

    public Func<AgentTaskResult, AgentTask, CancellationToken, Task>? OnTaskCompleted { get; set; }

    public AgentOrchestrator(IAgentClient agentClient, AppSettings settings)
    {
        _agentClient = agentClient;
        _settings = settings;
        _taskQueue = Channel.CreateBounded<AgentTask>(new BoundedChannelOptions(MaxQueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait
        });
    }

    public async Task<string> EnqueueTaskAsync(AgentTask task, CancellationToken cancellationToken = default)
    {
        _activeTasks[task.Id] = task;
        await _taskQueue.Writer.WriteAsync(task, cancellationToken);
        Log.Information("Agent task {TaskId} enqueued for user {UserId}", task.Id, task.DiscordUserId);
        return task.Id;
    }

    public AgentTask? GetTask(string taskId)
    {
        _activeTasks.TryGetValue(taskId, out var task);
        return task;
    }

    public Task StartProcessingAsync(CancellationToken cancellationToken)
    {
        _processingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _processingLoop = Task.Run(() => ProcessLoopAsync(_processingCts.Token), _processingCts.Token);
        Log.Information("Agent orchestrator started");
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        _taskQueue.Writer.TryComplete();
        _processingCts?.Cancel();
        if (_processingLoop != null)
        {
            try { await _processingLoop; }
            catch (OperationCanceledException) { }
        }
        Log.Information("Agent orchestrator stopped");
    }

    private async Task ProcessLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var task in _taskQueue.Reader.ReadAllAsync(cancellationToken))
                await ProcessSingleTaskAsync(task, cancellationToken);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error(ex, "Unexpected error in agent processing loop");
        }
    }

    private async Task ProcessSingleTaskAsync(AgentTask task, CancellationToken cancellationToken)
    {
        var timeoutMinutes = _settings.AgentSessionTimeoutMinutes > 0 ? _settings.AgentSessionTimeoutMinutes : 10;
        var maxRetries = _settings.AgentMaxRetries > 0 ? _settings.AgentMaxRetries : 3;

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(timeoutMinutes));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        task.Status = AgentTaskStatus.Running;
        Log.Information("Processing agent task {TaskId}", task.Id);

        try
        {
            var apiRequest = new AgentApiRequest
            {
                TaskId = task.Id,
                Prompt = task.Prompt,
                DocumentContent = task.PdfContent,
                Model = _settings.AnthropicAgentModel,
                MaxIterations = maxRetries,
                TimeoutSeconds = timeoutMinutes * 60
            };

            await _agentClient.SubmitTaskAsync(apiRequest, linkedCts.Token);
            var statusResponse = await PollForCompletionAsync(task.Id, linkedCts.Token);

            if (statusResponse == null)
            {
                task.Status = AgentTaskStatus.TimedOut;
                task.ErrorMessage = "Agent did not complete within the time limit.";
                await DeliverResultAsync(BuildTimeoutResult(task), task, cancellationToken);
                return;
            }

            if (statusResponse.Status != "completed")
            {
                task.Status = AgentTaskStatus.Failed;
                task.ErrorMessage = statusResponse.Error ?? "Agent returned an error.";
                await DeliverResultAsync(BuildErrorResult(task, statusResponse.Error), task, cancellationToken);
                return;
            }

            task.Status = AgentTaskStatus.Completed;
            task.CompletedAt = DateTime.UtcNow;

            // Fetch file contents via HTTP — no shared volume needed
            AgentFilesResponse? filesResponse = null;
            if (statusResponse.OutputFiles?.Count > 0)
            {
                filesResponse = await _agentClient.GetTaskFilesAsync(task.Id, linkedCts.Token);
                if (filesResponse == null)
                    Log.Warning("Task {TaskId}: could not fetch output files", task.Id);
            }

            var result = BuildCompletedResult(statusResponse, filesResponse);
            Log.Information("Task {TaskId} completed. Files: {FileCount}, Direct: {HasDirect}",
                task.Id, result.OutputFiles.Count, result.DirectResponse != null);
            await DeliverResultAsync(result, task, cancellationToken);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            task.Status = AgentTaskStatus.TimedOut;
            task.ErrorMessage = $"Task timed out after {timeoutMinutes} minutes.";
            try { await _agentClient.CancelTaskAsync(task.Id, cancellationToken); } catch { }
            await DeliverResultAsync(BuildTimeoutResult(task), task, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            task.Status = AgentTaskStatus.Cancelled;
        }
        catch (Exception ex)
        {
            task.Status = AgentTaskStatus.Failed;
            task.ErrorMessage = ex.Message;
            Log.Error(ex, "Task {TaskId} failed", task.Id);
            await DeliverResultAsync(BuildErrorResult(task, ex.Message), task, cancellationToken);
        }
        finally
        {
            _activeTasks.TryRemove(task.Id, out _);
        }
    }

    private async Task<AgentApiResponse?> PollForCompletionAsync(string taskId, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(PollInterval, cancellationToken);
            var status = await _agentClient.GetTaskStatusAsync(taskId, cancellationToken);
            if (status != null)
                return status;
        }
        return null;
    }

    private async Task DeliverResultAsync(AgentTaskResult result, AgentTask task, CancellationToken cancellationToken)
    {
        if (OnTaskCompleted == null)
        {
            Log.Warning("No delivery callback for task {TaskId}", task.Id);
            return;
        }
        try
        {
            await OnTaskCompleted(result, task, cancellationToken);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to deliver result for task {TaskId}", task.Id);
        }
    }

    private static AgentTaskResult BuildCompletedResult(AgentApiResponse status, AgentFilesResponse? files)
    {
        var result = new AgentTaskResult
        {
            TaskId = status.TaskId,
            Success = true,
            Summary = status.Message,
            DirectResponse = status.DirectResponse,
        };

        if (files?.Files != null)
            result.OutputFiles.AddRange(files.Files);

        return result;
    }

    private static AgentTaskResult BuildTimeoutResult(AgentTask task) => new()
    {
        TaskId = task.Id,
        Success = false,
        ErrorMessage = task.ErrorMessage ?? "Task timed out.",
        Summary = "The agent did not complete the task within the time limit."
    };

    private static AgentTaskResult BuildErrorResult(AgentTask task, string? error) => new()
    {
        TaskId = task.Id,
        Success = false,
        ErrorMessage = error ?? task.ErrorMessage ?? "Unknown error.",
        Summary = "The agent encountered an error while processing your task."
    };

    public void Dispose()
    {
        if (_disposed) return;
        _processingCts?.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}