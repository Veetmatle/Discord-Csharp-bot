using Newtonsoft.Json;
using Discord_Bot_AI.Models;
using Serilog;

namespace Discord_Bot_AI.Data;

/// <summary>
/// Registry for managing Discord user to Riot account mappings.
/// </summary>
public class UserRegistry : IUserRegistry
{
    private readonly string _filePath;
    private Dictionary<ulong, RiotAccount> _userMap = new();
    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);
    private bool _disposed;

    /// <summary>
    /// Creates a new UserRegistry with configurable data path for Docker volume support.
    /// </summary>
    /// <param name="dataPath">The directory path where user data will be stored.</param>
    public UserRegistry(string dataPath = ".")
    {
        Directory.CreateDirectory(dataPath);
        _filePath = Path.Combine(dataPath, "users.json");
        Load();
        Log.Information("UserRegistry initialized with data path: {DataPath}", dataPath);
    }

    /// <summary>
    /// Registers a Riot account for a Discord user on a specific guild.
    /// If the account already exists, adds the guild to the list of registered guilds.
    /// </summary>
    /// <param name="discordUserId">The Discord user's unique identifier.</param>
    /// <param name="account">The Riot account to register (used only for new registrations).</param>
    /// <param name="guildId">The guild ID where registration is happening.</param>
    public void RegisterUser(ulong discordUserId, RiotAccount account, ulong guildId)
    {
        _lock.EnterWriteLock();
        try
        {
            if (_userMap.TryGetValue(discordUserId, out var existingAccount))
            {
                if (!existingAccount.RegisteredGuildIds.Contains(guildId))
                {
                    existingAccount.RegisteredGuildIds.Add(guildId);
                    SaveInternal();
                    Log.Information("Added guild {GuildId} to existing account {PlayerName}", guildId, existingAccount.gameName);
                }
            }
            else
            {
                account.RegisteredGuildIds = new List<ulong> { guildId };
                _userMap[discordUserId] = account;
                SaveInternal();
                Log.Information("Registered new account {PlayerName} for guild {GuildId}", account.gameName, guildId);
            }
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Updates the last match ID for an existing account without full re-registration.
    /// </summary>
    public void UpdateLastMatchId(ulong discordUserId, string lastMatchId)
    {
        _lock.EnterWriteLock();
        try
        {
            if (_userMap.TryGetValue(discordUserId, out var account))
            {
                account.LastMatchId = lastMatchId;
                SaveInternal();
            }
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Updates the last TFT match ID for an existing account.
    /// </summary>
    public void UpdateLastTftMatchId(ulong discordUserId, string lastTftMatchId)
    {
        _lock.EnterWriteLock();
        try
        {
            if (_userMap.TryGetValue(discordUserId, out var account))
            {
                account.LastTftMatchId = lastTftMatchId;
                SaveInternal();
            }
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Updates the PUUID for an existing account. Used for API key migration.
    /// </summary>
    /// <param name="discordUserId">The Discord user's unique identifier.</param>
    /// <param name="newPuuid">The new PUUID obtained with current API key.</param>
    public void UpdateAccountPuuid(ulong discordUserId, string newPuuid)
    {
        _lock.EnterWriteLock();
        try
        {
            if (_userMap.TryGetValue(discordUserId, out var account))
            {
                var oldPuuid = account.puuid;
                account.puuid = newPuuid;
                account.LastMatchId = null;
                account.LastTftMatchId = null;
                SaveInternal();
                Log.Information("Migrated PUUID for {PlayerName}: {OldPuuid} -> {NewPuuid}", 
                    account.gameName, oldPuuid[..10] + "...", newPuuid[..10] + "...");
            }
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }
    
    /// <summary>
    /// Updates the TFT PUUID for an existing account. Used when TFT API key differs from League key.
    /// Clears LastTftMatchId when the PUUID actually changes to avoid false match notifications.
    /// </summary>
    public void UpdateAccountTftPuuid(ulong discordUserId, string tftPuuid)
    {
        _lock.EnterWriteLock();
        try
        {
            if (_userMap.TryGetValue(discordUserId, out var account))
            {
                if (account.TftPuuid == tftPuuid)
                    return;

                var old = account.TftPuuid;
                account.TftPuuid = tftPuuid;
                if (old != null)
                    account.LastTftMatchId = null;
                SaveInternal();
                Log.Information("Updated TFT PUUID for {PlayerName}: {Old} -> {New}",
                    account.gameName,
                    old != null ? old[..10] + "..." : "null",
                    tftPuuid[..10] + "...");
            }
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Removes a user's registration from a specific guild.
    /// If no guilds remain, removes the account entirely.
    /// </summary>
    /// <param name="discordUserId">The Discord user's unique identifier.</param>
    /// <param name="guildId">The guild ID to remove from registration.</param>
    /// <returns>True if the guild was removed from registration, false if not found.</returns>
    public bool RemoveUserFromGuild(ulong discordUserId, ulong guildId)
    {
        _lock.EnterWriteLock();
        try
        {
            if (!_userMap.TryGetValue(discordUserId, out var account))
            {
                return false;
            }

            if (!account.RegisteredGuildIds.Remove(guildId))
            {
                return false;
            }

            if (account.RegisteredGuildIds.Count == 0)
            {
                _userMap.Remove(discordUserId);
                Log.Information("Removed account {PlayerName} entirely - no guilds remaining", account.gameName);
            }
            else
            {
                Log.Information("Removed guild {GuildId} from account {PlayerName}", guildId, account.gameName);
            }

            SaveInternal();
            return true;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Retrieves a Riot account associated with a Discord user.
    /// </summary>
    public RiotAccount? GetAccount(ulong discordUserId)
    {
        _lock.EnterReadLock();
        try
        {
            return _userMap.GetValueOrDefault(discordUserId);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Returns a snapshot of all tracked users. Safe for iteration while other operations continue.
    /// </summary>
    /// <returns>A copy of all user-account pairs.</returns>
    public List<KeyValuePair<ulong, RiotAccount>> GetAllTrackedUsers()
    {
        _lock.EnterReadLock();
        try
        {
            return _userMap.ToList();
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
        string json = JsonConvert.SerializeObject(_userMap, Formatting.Indented);
        
        var tempPath = _filePath + ".tmp";
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, _filePath, overwrite: true);
    }

    /// <summary>
    /// Loads the user registry from a JSON file if it exists.
    /// </summary>
    private void Load()
    {
        if (File.Exists(_filePath))
        {
            string json = File.ReadAllText(_filePath);
            _userMap = JsonConvert.DeserializeObject<Dictionary<ulong, RiotAccount>>(json) ?? new();
            Log.Information("Loaded {Count} users from registry", _userMap.Count);
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