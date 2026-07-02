using Serilog;
namespace Discord_Bot_AI.Configuration;

/// <summary>
/// Enables configuration via environment variables (.env).
/// </summary>
public class EnvironmentConfigProvider : IConfigurationProvider
{
    public string? GetValue(string key)
    {
        return Environment.GetEnvironmentVariable(key);
    }
    public string GetRequiredValue(string key)
    {
        var value = Environment.GetEnvironmentVariable(key);
        if (string.IsNullOrWhiteSpace(value))
        {
            Log.Error("Required environment variable {Key} is not set", key);
            throw new InvalidOperationException($"Required environment variable '{key}' is not set.");
        }
        return value;
    }
    public List<string> GetValueAsList(string key, char separator = ',')
    {
        var value = Environment.GetEnvironmentVariable(key);
        if (string.IsNullOrWhiteSpace(value))
            return new List<string>();
        return value.Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }
}
