using Discord_Bot_AI.Models;

namespace Discord_Bot_AI.Data;

/// <summary>
/// Interface for guild configuration operations.
/// </summary>
public interface IGuildConfigRegistry : IDisposable
{
    void SetNotificationChannel(ulong guildId, ulong channelId);
    ulong? GetNotificationChannel(ulong guildId);
    void RemoveGuild(ulong guildId);
    GuildConfig? GetConfig(ulong guildId);
}
