using Discord_Bot_AI.Models;
using Serilog;

namespace Discord_Bot_AI.Services.Agent;

public class AgentService : IAgentService
{
    private readonly IPdfParser _pdfParser;
    private readonly IAgentOrchestrator _orchestrator;
    private readonly IPromptSanitizer _promptSanitizer;

    /// <summary>
    /// Callback for delivering completed task results to Discord.
    /// Delegates to the orchestrator's callback.
    /// </summary>
    public Func<AgentTaskResult, AgentTask, CancellationToken, Task>? OnTaskCompleted
    {
        get => _orchestrator.OnTaskCompleted;
        set => _orchestrator.OnTaskCompleted = value;
    }

    public AgentService(IPdfParser pdfParser, IAgentOrchestrator orchestrator, IPromptSanitizer promptSanitizer)
    {
        _pdfParser = pdfParser;
        _orchestrator = orchestrator;
        _promptSanitizer = promptSanitizer;
    }

    /// <summary>
    /// Handles a complete agent command: parses PDF, builds task, enqueues it.
    /// Returns task ID for user confirmation.
    /// </summary>
    public async Task<string> SubmitTaskAsync(
        ulong discordUserId,
        ulong guildId,
        ulong channelId,
        string prompt,
        Stream? pdfAttachment,
        string? pdfFileName,
        CancellationToken cancellationToken = default)
    {
        var validation = _promptSanitizer.Validate(prompt);
        if (!validation.IsValid)
        {
            Log.Warning("Agent task rejected for user {UserId}: {Reason}", discordUserId, validation.RejectionReason);
            throw new InvalidOperationException(validation.RejectionReason);
        }

        string? pdfContent = null;

        if (pdfAttachment != null)
        {
            Log.Information("Extracting text from PDF attachment: {FileName}", pdfFileName ?? "unknown");
            pdfContent = await _pdfParser.ExtractTextAsync(pdfAttachment, cancellationToken);

            if (string.IsNullOrWhiteSpace(pdfContent))
            {
                Log.Warning("PDF extraction returned no text for {FileName}. Task will proceed with prompt only.", pdfFileName);
                pdfContent = null;
            }
            else
            {
                Log.Debug("PDF extracted: {Length} characters from {FileName}", pdfContent.Length, pdfFileName);
            }
        }

        var task = new AgentTask
        {
            DiscordUserId = discordUserId,
            GuildId = guildId,
            ChannelId = channelId,
            Prompt = BuildAgentPrompt(prompt, pdfContent),
            PdfContent = pdfContent
        };

        var taskId = await _orchestrator.EnqueueTaskAsync(task, cancellationToken);
        Log.Information("Agent task {TaskId} submitted by user {UserId} in guild {GuildId}", taskId, discordUserId, guildId);

        return taskId;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        return _orchestrator.StartProcessingAsync(cancellationToken);
    }
    
    public Task StopAsync()
    {
        return _orchestrator.StopAsync();
    }

    /// <summary>
    /// Combines user prompt with extracted PDF content into a unified agent instruction.
    /// </summary>
    private static string BuildAgentPrompt(string userPrompt, string? pdfContent)
    {
        var systemRules = """
                          === SYSTEM RULES ===
                          1. You are an expert AI software engineer.
                          2. If your solution consists of 1-3 individual files, return their paths directly in the output files list.
                          3. If your solution requires a complex directory structure or many files (like scaffolding a full app), compress them into a 'project.zip' archive and return only the archive path.
                          ====================
                          """;
        
        if (string.IsNullOrWhiteSpace(pdfContent))
            return $"{systemRules}\n\nUser request:\n{userPrompt}";

        return $"""
                {systemRules}

                User request:
                {userPrompt}

                Attached document content:
                ---
                {pdfContent}
                ---

                Use the document content above as specification or reference material for the task.
                """;
    }
}
