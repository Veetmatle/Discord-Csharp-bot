namespace Discord_Bot_AI.Configuration;

/// <summary>
/// Application settings container loaded from environment variables or config file.
/// Immutable after creation for thread safety.
/// </summary>
public sealed class AppSettings
{
    public string DiscordToken { get; init; } = string.Empty;
    public string GeminiApiKey { get; init; } = string.Empty;
    public string RiotToken { get; init; } = string.Empty;
    public string RiotVersion { get; init; } = string.Empty;
    public string DataPath { get; init; } = "/app/data";
    public string CachePath { get; init; } = "/app/cache";
    public string LogPath { get; init; } = "/app/logs";

    /// <summary>
    /// Base URL of the OpenClaw agent container API.
    /// </summary>
    public string OpenClawBaseUrl { get; init; } = "http://openclaw:8080";

    /// <summary>
    /// Maximum time in minutes before an agent task is forcefully terminated.
    /// </summary>
    public int AgentSessionTimeoutMinutes { get; init; } = 10;

    /// <summary>
    /// Maximum tool rounds the agent may use per task.
    /// </summary>
    public int AgentMaxRetries { get; init; } = 5;

    /// <summary>
    /// Maximum number of concurrent agent tasks in the queue.
    /// </summary>
    public int AgentMaxConcurrentTasks { get; init; } = 2;

    /// <summary>
    /// Anthropic Claude model identifier used by the OpenClaw agent.
    /// </summary>
    public string AnthropicAgentModel { get; init; } = "claude-sonnet-4-20250514";

    /// <summary>
    /// Creates AppSettings from an IConfigurationProvider.
    /// </summary>
    public static AppSettings FromProvider(IConfigurationProvider provider)
    {
        return new AppSettings
        {
            DiscordToken = provider.GetRequiredValue("DISCORD_TOKEN"),
            GeminiApiKey = provider.GetRequiredValue("GEMINI_API_KEY"),
            RiotToken = provider.GetRequiredValue("RIOT_TOKEN"),
            RiotVersion = provider.GetValue("RIOT_VERSION") ?? "14.2.1",
            DataPath = provider.GetValue("DATA_PATH") ?? "/app/data",
            CachePath = provider.GetValue("CACHE_PATH") ?? "/app/cache",
            LogPath = provider.GetValue("LOG_PATH") ?? "/app/logs",
            OpenClawBaseUrl = provider.GetValue("OPENCLAW_BASE_URL") ?? "http://openclaw:8080",
            AgentSessionTimeoutMinutes = int.TryParse(provider.GetValue("AGENT_SESSION_TIMEOUT_MINUTES"), out var timeout) ? timeout : 10,
            AgentMaxRetries = int.TryParse(provider.GetValue("AGENT_MAX_RETRIES"), out var retries) ? retries : 5,
            AgentMaxConcurrentTasks = int.TryParse(provider.GetValue("AGENT_MAX_CONCURRENT_TASKS"), out var concurrent) ? concurrent : 2,
            AnthropicAgentModel = provider.GetValue("ANTHROPIC_MODEL") ?? "claude-sonnet-4-20250514"
        };
    }
}