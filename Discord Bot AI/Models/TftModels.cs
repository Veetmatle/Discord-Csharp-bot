using System.Text.Json.Serialization;

namespace Discord_Bot_AI.Models;

public class TftMatchData
{
    public TftMetadata metadata { get; set; } = new();
    public TftInfo info { get; set; } = new();
}

public class TftMetadata
{
    public string match_id { get; set; } = "";
    public List<string> participants { get; set; } = new();
}

public class TftInfo
{
    public long game_datetime { get; set; }
    public float game_length { get; set; }
    public string game_version { get; set; } = "";
    public string tft_game_type { get; set; } = "";
    public int tft_set_number { get; set; }
    public string tft_set_core_name { get; set; } = "";
    public List<TftParticipant> participants { get; set; } = new();
}

/// <summary>
/// A single player's data within a TFT match.
/// </summary>
public class TftParticipant
{
    public string puuid { get; set; } = "";
    public string riotIdGameName { get; set; } = "";
    public string riotIdTagline { get; set; } = "";
    public int placement { get; set; }
    public int level { get; set; }
    public int last_round { get; set; }
    public int players_eliminated { get; set; }
    public float time_eliminated { get; set; }
    public int total_damage_to_players { get; set; }
    public int gold_left { get; set; }
    public List<TftTrait> traits { get; set; } = new();
    public List<TftUnit> units { get; set; } = new();
    public TftCompanion companion { get; set; } = new();
    public List<string> augments { get; set; } = new();

    [JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? ExtensionData { get; set; }
}

public class TftTrait
{
    public string name { get; set; } = "";
    public int num_units { get; set; }
    public int tier_current { get; set; }
    public int tier_total { get; set; }
    public int style { get; set; }
}

public class TftUnit
{
    public string character_id { get; set; } = "";
    public string name { get; set; } = "";
    public int rarity { get; set; }
    public int tier { get; set; }
    public List<int> items { get; set; } = new();

    [JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? ExtensionData { get; set; }
}

/// <summary>
/// Companion (Little Legend) data
/// </summary>
public class TftCompanion
{
    public string content_ID { get; set; } = "";
    public string species { get; set; } = "";
    public int skin_ID { get; set; }
}

