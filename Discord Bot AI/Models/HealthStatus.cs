namespace Discord_Bot_AI.Models;

/// <summary>
/// Represents the health status of the bot for monitoring and Docker health checks.
/// </summary>
public class HealthStatus
{
    public bool IsHealthy { get; set; }
    public TimeSpan Uptime { get; set; }
    
    /// <summary>
    /// The current Discord connection state.
    /// </summary>
    public string ConnectionState { get; set; } = string.Empty;
    public int TrackedUsersCount { get; set; }
    public bool RiotApiRateLimited { get; set; }
    public bool GeminiApiRateLimited { get; set; }
}

/// <summary>
/// Statistics about the image cache.
/// </summary>
public class CacheStats
{
    public int FileCount { get; set; }
    public double TotalSizeMB { get; set; }
}

