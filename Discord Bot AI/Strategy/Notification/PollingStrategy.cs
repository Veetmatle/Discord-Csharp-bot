using Discord_Bot_AI.Services;
using Discord_Bot_AI.Data;
using Discord_Bot_AI.Models;

namespace Discord_Bot_AI.Strategy.Notification;

public class PollingStrategy : IMatchNotification
{
    private readonly RiotService _riot;
    private readonly UserRegistry _userRegistry;
    private readonly Func<RiotAccount, MatchData, Task> _onNewMatchFound;

    public PollingStrategy(RiotService riot, UserRegistry userRegistry, Func<RiotAccount, MatchData, Task> onNewMatchFound)
    {
        _riot = riot;
        _userRegistry = userRegistry;
        _onNewMatchFound = onNewMatchFound; 
    }

    public async Task StartMonitoringAsync()
    {
        using PeriodicTimer timer = new PeriodicTimer(TimeSpan.FromMinutes(10));
        while (await timer.WaitForNextTickAsync())
        {
            await CheckMatchesInternalAsync();
        }
    }

    private async Task CheckMatchesInternalAsync()
    {
        var users = _userRegistry.GetAllTrackedUsers();
        foreach (var entry in users)
        {
            var account = entry.Value;
            string? currentMatchId = await _riot.GetLatestMatchIdAsync(account.puuid);

            if (!string.IsNullOrEmpty(currentMatchId) && currentMatchId != account.LastMatchId)
            {
                var matchData = await _riot.GetMatchDetailsAsync(currentMatchId);
                if (matchData != null)
                {
                    await _onNewMatchFound(account, matchData);
                }
            }
        }
    }
}