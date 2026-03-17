using System.Text.Json.Serialization;

namespace Discord_Bot_AI.Models;

/// <summary>
/// Represents a single task submitted to the AI agent for processing.
/// </summary>
public class AgentTask
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public ulong DiscordUserId { get; set; }
    public ulong ChannelId { get; set; }
    public ulong GuildId { get; set; }
    public string Prompt { get; set; } = string.Empty;
    public string? PdfContent { get; set; }
    public AgentTaskStatus Status { get; set; } = AgentTaskStatus.Queued;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
    public string? ErrorMessage { get; set; }
    public int RetryCount { get; set; }
}

/// <summary>
/// Lifecycle states of an agent task from submission to completion.
/// </summary>
public enum AgentTaskStatus
{
    Queued,
    Running,
    Completed,
    Failed,
    TimedOut,
    Cancelled
}

/// <summary>
/// Result payload returned by the agent after task execution.
/// </summary>
public class AgentTaskResult
{
    public string TaskId { get; set; } = string.Empty;
    public bool Success { get; set; }
    public List<AgentOutputFile> OutputFiles { get; set; } = new();
    public string? ErrorMessage { get; set; }
    public string? Summary { get; set; }

    /// <summary>
    /// Plain-text answer returned by the agent instead of a file.
    /// </summary>
    public string? DirectResponse { get; set; }
}

/// <summary>
/// Represents a single output file produced by the agent.
/// File content is transferred as base64 over HTTP — no shared volume needed.
/// </summary>
public class AgentOutputFile
{
    public string FileName { get; set; } = string.Empty;
    public long SizeBytes { get; set; }

    /// <summary>
    /// Base64-encoded file content. Null if file exceeded size limit (TooLarge=true).
    /// </summary>
    public string? ContentBase64 { get; set; }

    /// <summary>
    /// True if file exceeded MAX_INLINE_FILE_BYTES on the agent side.
    /// </summary>
    public bool TooLarge { get; set; }

    /// <summary>
    /// Decode ContentBase64 to raw bytes. Returns empty array if content is missing.
    /// </summary>
    public byte[] GetBytes() =>
        ContentBase64 is not null ? Convert.FromBase64String(ContentBase64) : Array.Empty<byte>();
}

/// <summary>
/// HTTP request body sent to the agent container API.
/// </summary>
public class AgentApiRequest
{
    [JsonPropertyName("TaskId")]
    public string TaskId { get; set; } = string.Empty;

    [JsonPropertyName("Prompt")]
    public string Prompt { get; set; } = string.Empty;

    [JsonPropertyName("DocumentContent")]
    public string? DocumentContent { get; set; }

    [JsonPropertyName("Model")]
    public string Model { get; set; } = "claude-sonnet-4-20250514";

    [JsonPropertyName("MaxIterations")]
    public int MaxIterations { get; set; } = 7;

    [JsonPropertyName("TimeoutSeconds")]
    public int TimeoutSeconds { get; set; } = 600;
}

/// <summary>
/// HTTP response from GET /tasks/{id} — status and metadata only, no file content.
/// </summary>
public class AgentApiResponse
{
    [JsonPropertyName("TaskId")]
    public string TaskId { get; set; } = string.Empty;

    [JsonPropertyName("Status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("Message")]
    public string? Message { get; set; }

    [JsonPropertyName("OutputFiles")]
    public List<AgentFileMetadata>? OutputFiles { get; set; }

    [JsonPropertyName("DirectResponse")]
    public string? DirectResponse { get; set; }

    [JsonPropertyName("Error")]
    public string? Error { get; set; }
}

/// <summary>
/// File metadata returned in GET /tasks/{id} — name and size only.
/// </summary>
public class AgentFileMetadata
{
    [JsonPropertyName("FileName")]
    public string FileName { get; set; } = string.Empty;

    [JsonPropertyName("SizeBytes")]
    public long SizeBytes { get; set; }
}

/// <summary>
/// HTTP response from GET /tasks/{id}/files — full file contents as base64.
/// </summary>
public class AgentFilesResponse
{
    [JsonPropertyName("TaskId")]
    public string TaskId { get; set; } = string.Empty;

    [JsonPropertyName("Files")]
    public List<AgentOutputFile> Files { get; set; } = new();
}