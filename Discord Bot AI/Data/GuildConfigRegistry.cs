using Newtonsoft.Json;
using Discord_Bot_AI.Models;
using Serilog;

namespace Discord_Bot_AI.Data;

/// <summary>
/// Thread-safe registry for managing Discord guild configurations.
/// </summary>
public class GuildConfigRegistry : IGuildConfigRegistry
{
    private readonly string _filePath;
    private Dictionary<ulong, GuildConfig> _guildConfigs = new();
    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);
    private bool _disposed;

    /// <summary>
    /// Creates a new GuildConfigRegistry with configurable data path for Docker volume support.
    /// </summary>
    /// <param name="dataPath">The directory path where guild data will be stored.</param>
    public GuildConfigRegistry(string dataPath = ".")
    {
        Directory.CreateDirectory(dataPath);
        _filePath = Path.Combine(dataPath, "guilds.json");
        Load();
        Log.Information("GuildConfigRegistry initialized with data path: {DataPath}", dataPath);
    }

    /// <summary>
    /// Sets or updates the notification channel for a guild.
    /// </summary>
    public void SetNotificationChannel(ulong guildId, ulong channelId)
    {
        _lock.EnterWriteLock();
        try
        {
            if (!_guildConfigs.TryGetValue(guildId, out var config))
            {
                config = new GuildConfig();
                _guildConfigs[guildId] = config;
            }
            config.NotificationChannelId = channelId;
            SaveInternal();
            Log.Information("Set notification channel {ChannelId} for guild {GuildId}", channelId, guildId);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Gets the configured notification channel for a guild.
    /// </summary>
    public ulong? GetNotificationChannel(ulong guildId)
    {
        _lock.EnterReadLock();
        try
        {
            if (_guildConfigs.TryGetValue(guildId, out var config) && config.NotificationChannelId != 0)
            {
                return config.NotificationChannelId;
            }
            return null;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Removes all configuration for a guild when bot leaves.
    /// </summary>
    /// <param name="guildId">The Discord guild ID.</param>
    public void RemoveGuild(ulong guildId)
    {
        _lock.EnterWriteLock();
        try
        {
            if (_guildConfigs.Remove(guildId))
            {
                SaveInternal();
                Log.Information("Removed configuration for guild {GuildId}", guildId);
            }
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Gets the full configuration for a guild.
    /// </summary>
    public GuildConfig? GetConfig(ulong guildId)
    {
        _lock.EnterReadLock();
        try
        {
            return _guildConfigs.GetValueOrDefault(guildId);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }
    
    /// <summary>
    /// Gets all guild IDs that have a notification channel configured.
    /// </summary>
    public List<ulong> GetAllConfiguredGuildIds()
    {
        _lock.EnterReadLock();
        try
        {
            return _guildConfigs
                .Where(kvp => kvp.Value.NotificationChannelId != 0)
                .Select(kvp => kvp.Key)
                .ToList();
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Internal save method - must be called within a write lock.
    /// </summary>
    private void SaveInternal()
    {
        string json = JsonConvert.SerializeObject(_guildConfigs, Formatting.Indented);
        
        var tempPath = _filePath + ".tmp";
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, _filePath, overwrite: true);
    }

    /// <summary>
    /// Loads guild configurations from a JSON file if it exists.
    /// </summary>
    private void Load()
    {
        if (File.Exists(_filePath))
        {
            string json = File.ReadAllText(_filePath);
            _guildConfigs = JsonConvert.DeserializeObject<Dictionary<ulong, GuildConfig>>(json) ?? new();
            Log.Information("Loaded {Count} guild configurations", _guildConfigs.Count);
        }
    }
    
    public void Dispose()
    {
        if (_disposed) return;
        _lock.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
