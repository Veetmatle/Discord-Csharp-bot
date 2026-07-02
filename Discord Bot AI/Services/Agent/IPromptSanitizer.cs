namespace Discord_Bot_AI.Services.Agent;

/// <summary>
/// Validates and sanitizes user prompts before they reach the AI agent.
/// Rejects prompts that contain dangerous instructions (e.g. shell injection, infinite loops).
/// </summary>
public interface IPromptSanitizer
{
    PromptValidationResult Validate(string prompt);
}

/// <summary>
/// Result of prompt validation with rejection reason if applicable.
/// </summary>
public sealed class PromptValidationResult
{
    public bool IsValid { get; init; }
    public string? RejectionReason { get; init; }

    public static PromptValidationResult Ok() => new() { IsValid = true };
    public static PromptValidationResult Rejected(string reason) => new() { IsValid = false, RejectionReason = reason };
}
