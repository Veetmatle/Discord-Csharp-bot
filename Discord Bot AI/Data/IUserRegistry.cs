using Discord_Bot_AI.Models;
namespace Discord_Bot_AI.Data;

/// <summary>
/// Interface for user registry operations following Interface Segregation Principle.
/// Manages user-to-Riot-account mappings only.
/// </summary>
public interface IUserRegistry : IDisposable
{
    void RegisterUser(ulong discordUserId, RiotAccount account, ulong guildId);
    void UpdateLastMatchId(ulong discordUserId, string lastMatchId);
    bool RemoveUserFromGuild(ulong discordUserId, ulong guildId);
    RiotAccount? GetAccount(ulong discordUserId);
    List<KeyValuePair<ulong, RiotAccount>> GetAllTrackedUsers();
}
