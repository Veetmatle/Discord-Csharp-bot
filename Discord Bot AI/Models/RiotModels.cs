namespace Discord_Bot_AI.Models;

/// <summary>
/// Represents a Riot Games account linked to a Discord user.
/// </summary>
public class RiotAccount
{
    public string puuid { get; set; }
    public string gameName { get; set; }
    public string tagLine { get; set; }
    public string? LastMatchId { get; set; }
    
    /// <summary>
    /// List of Discord Guild (server) IDs where this account is registered.
    /// Allows the same account to receive notifications on multiple servers.
    /// </summary>
    public List<ulong> RegisteredGuildIds { get; set; } = new();
}

/// <summary>
/// Configuration for a Discord Guild (server), storing the notification channel ID.
/// </summary>
public class GuildConfig
{
    public ulong NotificationChannelId { get; set; }
}

public class MatchData
{
    public Metadata metadata { get; set; }
    public Info info { get; set; }
}

public class Metadata
{
    public string matchId { get; set; }
    public List<string> participants { get; set; }
}

public class Info
{
    public long gameDuration { get; set; } 
    public string gameMode { get; set; }
    public List<Participant> participants { get; set; }
}

public class Participant
{
    public string puuid { get; set; }
    public string summonerName { get; set; }
    public string riotIdGameName { get; set; }
    
    public string riotIdTagline { get; set; }
    public string? teamPosition { get; set; }
    
    public int kills { get; set; }
    public int deaths { get; set; }
    public int assists { get; set; }
    public string championName { get; set; }
    public bool win { get; set; }
    
    // ITEMS: item0-item5 are main inventory slots, item6 is trinket (ward)
    // roleBoundItem is the role quest slot (boots for ADC after quest completion, etc.)
    public int item0 { get; set; }
    public int item1 { get; set; }
    public int item2 { get; set; }
    public int item3 { get; set; }
    public int item4 { get; set; }
    public int item5 { get; set; }
    public int item6 { get; set; }  // Trinket slot
    public int roleBoundItem { get; set; }
    
    public int champLevel { get; set; }
    
    /// <summary>
    /// Alias for champLevel to match renderer expectations.
    /// </summary>
    public int level => champLevel;
    
    // Additional stats
    public int totalMinionsKilled { get; set; } // All minions
    public int neutralMinionsKilled { get; set; } // Jungle minions
    public int goldEarned { get; set; }
    public int totalDamageDealtToChampions { get; set; } 
    
    /// <summary>
    /// Captures any unmapped JSON fields from Riot API for diagnostic purposes.
    /// </summary>
    [System.Text.Json.Serialization.JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? ExtensionData { get; set; }
}