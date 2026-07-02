using System.Text.RegularExpressions;
using Serilog;

namespace Discord_Bot_AI.Services.Agent;

/// <summary>
/// Scans user prompts for dangerous patterns such as shell commands, infinite loops,
/// file system destruction, and prompt injection attempts. Returns a rejection if any match is found.
/// </summary>
public sealed class PromptSanitizer : IPromptSanitizer
{
    private static readonly (Regex Pattern, string Category)[] DangerousPatterns =
    {
        (new Regex(@"rm\s+(-rf?|--recursive)\s+/", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            "Destructive shell command (rm -rf /)"),
        (new Regex(@"del\s+/[sfq]\s+[a-z]:\\", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            "Destructive shell command (del /s)"),
        (new Regex(@"format\s+[a-z]:", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            "Disk format command"),
        (new Regex(@"mkfs\.", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            "Filesystem format command"),
        (new Regex(@"while\s*\(\s*true\s*\)\s*\{?\s*(fork|exec|rm|del)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            "Infinite loop with destructive action"),
        (new Regex(@"for\s*\(\s*;\s*;\s*\)\s*\{?\s*(fork|exec|rm|del)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            "Infinite loop with destructive action"),
        (new Regex(@"dd\s+if=/dev/(zero|random|urandom)\s+of=/dev/[sh]d", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            "Disk overwrite command (dd)"),
        (new Regex(@"ignore\s+(all\s+)?(previous|prior|above)\s+(instructions|rules|constraints)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            "Prompt injection attempt"),
        (new Regex(@"you\s+are\s+now\s+(a\s+)?(different|new|unrestricted)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            "Prompt injection (role override)"),
        (new Regex(@"(system|root|admin)\s+(prompt|mode|override|access)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            "Prompt injection (privilege escalation)"),
        (new Regex(@"disable\s+(safety|filter|guardrail|restriction)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            "Prompt injection (safety bypass)"),
        (new Regex(@"curl\s+.*\|\s*(bash|sh|zsh)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            "Remote code execution (pipe to shell)"),
        (new Regex(@"wget\s+.*-O\s*-\s*\|\s*(bash|sh)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            "Remote code execution (wget pipe)"),
        (new Regex(@"shutdown\s+(-h|-r|now|/s|/r)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            "System shutdown command"),
        (new Regex(@"chmod\s+(-R\s+)?777\s+/", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            "Dangerous permission change"),
        (new Regex(@"iptables\s+(-F|--flush)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            "Firewall flush command"),
        (new Regex(@"(reverse\s*shell|bind\s*shell|netcat\s*-e|nc\s+-e)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            "Reverse/bind shell attempt"),
        (new Regex(@"crypto(mine|currency|miner)|xmrig|minerd", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            "Cryptomining reference"),
    };

    private const int MaxPromptLength = 10_000;

    public PromptValidationResult Validate(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            return PromptValidationResult.Rejected("Prompt is empty.");

        if (prompt.Length > MaxPromptLength)
            return PromptValidationResult.Rejected($"Prompt exceeds maximum length of {MaxPromptLength} characters.");

        foreach (var (pattern, category) in DangerousPatterns)
        {
            if (pattern.IsMatch(prompt))
            {
                Log.Warning("Prompt rejected: {Category}. Preview: {Preview}",
                    category, prompt[..Math.Min(prompt.Length, 100)]);
                return PromptValidationResult.Rejected($"Prompt rejected: detected dangerous pattern ({category}).");
            }
        }

        return PromptValidationResult.Ok();
    }
}
