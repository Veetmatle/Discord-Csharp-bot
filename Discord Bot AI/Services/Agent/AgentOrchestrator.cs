using System.Collections.Concurrent;
using System.Threading.Channels;
using Discord_Bot_AI.Configuration;
using Discord_Bot_AI.Models;
using Serilog;

namespace Discord_Bot_AI.Services.Agent;

/// <summary>
/// Manages the agent task queue using Channel&lt;T&gt; for backpressure-aware buffering.
/// Processes tasks sequentially, polls for results with timeout, and enforces retry limits.
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

    /// <summary>
    /// Callback invoked when a task completes. Set by BotService for Discord delivery.
    /// </summary>
    public Func<AgentTaskResult, AgentTask, CancellationToken, Task>? OnTaskCompleted { get; set; }

    /// <summary>
    /// Initializes the orchestrator with a bounded channel for task queuing.
    /// </summary>
    public AgentOrchestrator(IAgentClient agentClient, AppSettings settings)
    {
        _agentClient = agentClient;
        _settings = settings;

        _taskQueue = Channel.CreateBounded<AgentTask>(new BoundedChannelOptions(MaxQueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait
        });
    }

    /// <summary>
    /// Adds a task to the processing queue. Blocks if queue is full.
    /// </summary>
    public async Task<string> EnqueueTaskAsync(AgentTask task, CancellationToken cancellationToken = default)
    {
        _activeTasks[task.Id] = task;
        await _taskQueue.Writer.WriteAsync(task, cancellationToken);
        Log.Information("Agent task {TaskId} enqueued for user {UserId}", task.Id, task.DiscordUserId);
        return task.Id;
    }

    /// <summary>
    /// Returns a task by its ID, or null if not found.
    /// </summary>
    public AgentTask? GetTask(string taskId)
    {
        _activeTasks.TryGetValue(taskId, out var task);
        return task;
    }

    /// <summary>
    /// Starts the background loop that reads tasks from the channel and processes them.
    /// </summary>
    public Task StartProcessingAsync(CancellationToken cancellationToken)
    {
        _processingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _processingLoop = Task.Run(() => ProcessLoopAsync(_processingCts.Token), _processingCts.Token);
        Log.Information("Agent orchestrator started");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Stops the processing loop and cancels any in-flight tasks.
    /// </summary>
    public async Task StopAsync()
    {
        _taskQueue.Writer.TryComplete();
        _processingCts?.Cancel();

        if (_processingLoop != null)
        {
            try
            {
                await _processingLoop;
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown
            }
        }

        Log.Information("Agent orchestrator stopped");
    }

    /// <summary>
    /// Main processing loop: dequeues tasks, submits to agent, polls for results.
    /// </summary>
    private async Task ProcessLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var task in _taskQueue.Reader.ReadAllAsync(cancellationToken))
            {
                await ProcessSingleTaskAsync(task, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            Log.Debug("Agent processing loop cancelled");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Unexpected error in agent processing loop");
        }
    }

    /// <summary>
    /// Submits a single task to the agent, polls for completion, enforces timeout and retry limits.
    /// </summary>
    private async Task ProcessSingleTaskAsync(AgentTask task, CancellationToken cancellationToken)
    {
        var timeoutMinutes = _settings.AgentSessionTimeoutMinutes > 0
            ? _settings.AgentSessionTimeoutMinutes
            : 10;
        var maxRetries = _settings.AgentMaxRetries > 0
            ? _settings.AgentMaxRetries
            : 3;

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
                Model = _settings.GeminiAgentModel,
                MaxIterations = maxRetries,
                TimeoutSeconds = timeoutMinutes * 60
            };

            await _agentClient.SubmitTaskAsync(apiRequest, linkedCts.Token);

            var result = await PollForCompletionAsync(task.Id, linkedCts.Token);

            if (result == null)
            {
                task.Status = AgentTaskStatus.TimedOut;
                task.ErrorMessage = "Agent did not complete within the time limit.";
                Log.Warning("Task {TaskId} finished with status: {Status}. Agent did not respond in time.", 
                    task.Id, task.Status);
                await DeliverResultAsync(BuildTimeoutResult(task), task, cancellationToken);
            }
            else if (result.Status == "completed")
            {
                task.Status = AgentTaskStatus.Completed;
                task.CompletedAt = DateTime.UtcNow;
                Log.Information("Task {TaskId} finished with status: {Status}. Output files: {FileCount}", 
                    task.Id, task.Status, result.OutputFiles?.Count ?? 0);
                await DeliverResultAsync(MapApiResponseToResult(result), task, cancellationToken);
            }
            else
            {
                task.Status = AgentTaskStatus.Failed;
                task.ErrorMessage = result.Error ?? "Agent returned an error.";
                Log.Error("Task {TaskId} finished with status: {Status}. Error from Agent: {ErrorMessage}", 
                    task.Id, task.Status, task.ErrorMessage);
                await DeliverResultAsync(BuildErrorResult(task, result.Error), task, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            task.Status = AgentTaskStatus.TimedOut;
            task.ErrorMessage = $"Task timed out after {timeoutMinutes} minutes.";
            Log.Warning("Agent task {TaskId} timed out", task.Id);

            try
            {
                await _agentClient.CancelTaskAsync(task.Id, cancellationToken);
            }
            catch
            {
                // Best-effort cancel
            }

            await DeliverResultAsync(BuildTimeoutResult(task), task, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            task.Status = AgentTaskStatus.Cancelled;
            Log.Debug("Agent task {TaskId} cancelled", task.Id);
        }
        catch (Exception ex)
        {
            task.Status = AgentTaskStatus.Failed;
            task.ErrorMessage = ex.Message;
            Log.Error(ex, "Task {TaskId} finished with status: {Status}. Error from Agent: {ErrorMessage}", 
                task.Id, task.Status, task.ErrorMessage);
            await DeliverResultAsync(BuildErrorResult(task, ex.Message), task, cancellationToken);
        }
        finally
        {
            _activeTasks.TryRemove(task.Id, out _);
        }
    }

    /// <summary>
    /// Polls the agent container at regular intervals until the task is done or cancelled.
    /// </summary>
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

    /// <summary>
    /// Invokes the OnTaskCompleted callback to deliver results to Discord.
    /// </summary>
    private async Task DeliverResultAsync(AgentTaskResult result, AgentTask task, CancellationToken cancellationToken)
    {
        if (OnTaskCompleted == null)
        {
            Log.Warning("No delivery callback set for agent task {TaskId}", task.Id);
            return;
        }

        try
        {
            Log.Information("Delivering result for task {TaskId} to Discord. Success: {Success}, Summary: {Summary}", 
                task.Id, result.Success, result.Summary ?? "No summary");
            
            if (!result.Success)
            {
                Log.Warning("Task {TaskId} failed delivery contains error: {Error}", 
                    task.Id, result.ErrorMessage ?? "No error message");
            }
            
            await OnTaskCompleted(result, task, cancellationToken);
            
            Log.Information("Successfully delivered result for task {TaskId} to Discord", task.Id);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to deliver agent task result {TaskId} to Discord", task.Id);
        }
    }

    private static AgentTaskResult MapApiResponseToResult(AgentApiResponse response)
    {
        var result = new AgentTaskResult
        {
            TaskId = response.TaskId,
            Success = true,
            Summary = response.Message
        };

        if (response.OutputFiles != null)
        {
            foreach (var filePath in response.OutputFiles)
            {
                var fi = new FileInfo(filePath);
                if (fi.Exists)
                {
                    result.OutputFiles.Add(new AgentOutputFile
                    {
                        FileName = fi.Name,
                        FilePath = fi.FullName,
                        SizeBytes = fi.Length
                    });
                }
            }
        }

        return result;
    }

    private static AgentTaskResult BuildTimeoutResult(AgentTask task)
    {
        return new AgentTaskResult
        {
            TaskId = task.Id,
            Success = false,
            ErrorMessage = task.ErrorMessage ?? "Task timed out.",
            Summary = "The agent did not complete the task within the time limit."
        };
    }

    private static AgentTaskResult BuildErrorResult(AgentTask task, string? error)
    {
        return new AgentTaskResult
        {
            TaskId = task.Id,
            Success = false,
            ErrorMessage = error ?? task.ErrorMessage ?? "Unknown error.",
            Summary = "The agent encountered an error while processing your task."
        };
    }

    /// <summary>
    /// Disposes the processing CancellationTokenSource.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _processingCts?.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
