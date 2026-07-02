using Discord_Bot_AI.Services;
using Discord_Bot_AI.Models;
using Discord_Bot_AI.Data;

namespace Discord_Bot_AI.Strategy.Notification;

/// <summary>
/// Command-based strategy for on-demand match retrieval triggered by user commands.
/// </summary>
public class CommandStrategy : IMatchNotification
{
    private readonly RiotService _riot;
    private readonly IUserRegistry _userRegistry;
    private readonly Func<RiotAccount, MatchData, CancellationToken, Task> _onMatchFound;

    /// <summary>
    /// Creates a new command strategy instance.
    /// </summary>
    public CommandStrategy(RiotService riot, IUserRegistry registry, Func<RiotAccount, MatchData, CancellationToken, Task> onMatchFound)
    {
        _riot = riot;
        _userRegistry = registry;
        _onMatchFound = onMatchFound;
    }
    
    /// <summary>
    /// Command strategy doesn't require background monitoring.
    /// </summary>
    public Task StartMonitoringAsync() => Task.CompletedTask;
    
    /// <summary>
    /// Command strategy has no background process to stop.
    /// </summary>
    public Task StopMonitoringAsync() => Task.CompletedTask;
    
    /// <summary>
    /// Checks and retrieves the latest match data for a specific Discord user on demand.
    /// </summary>
    public async Task CheckUserMatchAsync(ulong discordId, CancellationToken cancellationToken = default)
    {
        var account = _userRegistry.GetAccount(discordId);
        if (account == null) return;

        cancellationToken.ThrowIfCancellationRequested();
        
        string? currentMatchId = await _riot.GetLatestMatchIdAsync(account.puuid, cancellationToken);
        
        if (!string.IsNullOrEmpty(currentMatchId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            
            var matchData = await _riot.GetMatchDetailsAsync(currentMatchId, cancellationToken);
            if (matchData != null)
            {
                await _onMatchFound(account, matchData, cancellationToken);
            }
        }
    }
}