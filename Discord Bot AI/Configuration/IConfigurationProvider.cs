namespace Discord_Bot_AI.Configuration;

/// <summary>
/// Allows different configuration sources (env, file, etc.) to be used interchangeably.
/// </summary>
public interface IConfigurationProvider
{
    /// <summary>
    /// Retrieves a configuration value by key (optional or default, null if not found).
    /// </summary>
    string? GetValue(string key);
    
    /// <summary>
    /// Retrieves a required configuration value, throwing exception if not found.
    /// </summary>
    string GetRequiredValue(string key);
    
    /// <summary>
    /// Retrieves a configuration value as a list of strings.
    /// </summary>
    List<string> GetValueAsList(string key, char separator = ',');
}
