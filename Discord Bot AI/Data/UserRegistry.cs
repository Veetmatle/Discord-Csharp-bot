using Newtonsoft.Json;
using Discord_Bot_AI.Models;

namespace Discord_Bot_AI.Data;

public class UserRegistry
{
    private const string FilePath = "users.json";
    private Dictionary<ulong, RiotAccount> _userMap = new();

    public UserRegistry()
    {
        Load();
    }

    public void RegisterUser(ulong discordUserId, RiotAccount account)
    {
        _userMap[discordUserId] = account;
        Save();
    }
    
    public bool RemoveUser(ulong discordUserId)
    {
        if (_userMap.Remove(discordUserId))
        {
            Save(); 
            return true;
        }
        return false;
    }

    public RiotAccount? GetAccount(ulong discordUserId)
    {
        return _userMap.TryGetValue(discordUserId, out var account) ? account : null;
    }

    public List<KeyValuePair<ulong, RiotAccount>> GetAllTrackedUsers()
    {
        return _userMap.ToList();
    }

    private void Save()
    {
        string json = JsonConvert.SerializeObject(_userMap, Formatting.Indented);
        File.WriteAllText(FilePath, json);
    }

    private void Load()
    {
        if (File.Exists(FilePath))
        {
            string json = File.ReadAllText(FilePath);
            _userMap = JsonConvert.DeserializeObject<Dictionary<ulong, RiotAccount>>(json) ?? new();
        }
    }
}