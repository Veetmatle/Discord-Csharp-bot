using Discord_Bot_AI.Services;
using Discord_Bot_AI.Data;
using Discord_Bot_AI.Models;
using Serilog;

namespace Discord_Bot_AI.Strategy.Notification;

/// <summary>
/// Polling-based strategy for detecting new LoL and TFT matches by periodically checking the Riot API.
/// Implements graceful shutdown and rate limit awareness.
/// </summary>
public class PollingStrategy : IMatchNotification
{
    private readonly RiotService _riot;
    private readonly IUserRegistry _userRegistry;
    private readonly Func<RiotAccount, MatchData, CancellationToken, Task> _onNewMatchFound;
    private readonly Func<RiotAccount, TftMatchData, CancellationToken, Task>? _onNewTftMatchFound;
    private CancellationTokenSource? _cts;
    
    private static readonly TimeSpan PollingInterval = TimeSpan.FromMinutes(7);
    private static readonly TimeSpan DelayBetweenUsers = TimeSpan.FromMilliseconds(1100);
    private static readonly TimeSpan InitialStartupDelay = TimeSpan.FromSeconds(15);
    private const int MaxConcurrentChecks = 3;

    /// <summary>
    /// Creates a new polling strategy with LoL and optional TFT match callbacks.
    /// </summary>
    public PollingStrategy(
        RiotService riot,
        IUserRegistry userRegistry,
        Func<RiotAccount, MatchData, CancellationToken, Task> onNewMatchFound,
        Func<RiotAccount, TftMatchData, CancellationToken, Task>? onNewTftMatchFound = null)
    {
        _riot = riot;
        _userRegistry = userRegistry;
        _onNewMatchFound = onNewMatchFound;
        _onNewTftMatchFound = onNewTftMatchFound;
    }

    /// <summary>
    /// Starts the background monitoring loop. Call StopMonitoringAsync to gracefully stop.
    /// </summary>
    public async Task StartMonitoringAsync()
    {
        _cts = new CancellationTokenSource();
        
        try
        {
            Log.Information("Polling strategy starting. Waiting {Delay}s before first check...", 
                InitialStartupDelay.TotalSeconds);
            await Task.Delay(InitialStartupDelay, _cts.Token);
            
            using PeriodicTimer timer = new PeriodicTimer(PollingInterval);
            await CheckMatchesInternalAsync(_cts.Token);
            
            while (await timer.WaitForNextTickAsync(_cts.Token))
            {
                await CheckMatchesInternalAsync(_cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            Log.Information("Polling monitoring stopped gracefully");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Unexpected error in polling monitoring loop");
        }
    }
    
    /// <summary>
    /// Signals the monitoring loop to stop gracefully.
    /// </summary>
    public Task StopMonitoringAsync()
    {
        _cts?.Cancel();
        return Task.CompletedTask;
    }
    
    /// <summary>
    /// Periodically checks for new matches across all tracked users using parallel processing with a concurrency limit.
    /// </summary>
    private async Task CheckMatchesInternalAsync(CancellationToken cancellationToken)
    {
        if (_riot.IsRateLimited)
        {
            Log.Debug("Skipping polling check - Riot API is rate limited");
            return;
        }
        
        var users = _userRegistry.GetAllTrackedUsers();
        
        if (users.Count == 0)
        {
            return;
        }
        
        Log.Information("Checking {Count} users for new matches", users.Count);
        
        using var semaphore = new SemaphoreSlim(MaxConcurrentChecks);
        var tasks = new List<Task>();

        foreach (var entry in users)
        {
            cancellationToken.ThrowIfCancellationRequested();
            
            var discordUserId = entry.Key;
            var account = entry.Value;
            
            tasks.Add(ProcessUserWithSemaphoreAsync(semaphore, discordUserId, account, cancellationToken));
            
            await Task.Delay(DelayBetweenUsers / MaxConcurrentChecks, cancellationToken);
        }
        
        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error during batch processing");
        }
    }

    /// <summary>
    /// Wraps user processing with semaphore acquisition for concurrency control.
    /// </summary>
    private async Task ProcessUserWithSemaphoreAsync(SemaphoreSlim semaphore, ulong discordUserId, RiotAccount account, CancellationToken cancellationToken)
    {
        await semaphore.WaitAsync(cancellationToken);
        try
        {
            await ProcessSingleUserMatchAsync(discordUserId, account, cancellationToken);
        }
        finally
        {
            semaphore.Release();
        }
    }

    /// <summary>
    /// Fetches the latest LoL and TFT matches for a specific account and triggers notifications for new ones.
    /// </summary>
    private async Task ProcessSingleUserMatchAsync(ulong discordUserId, RiotAccount account, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            
            string? currentMatchId = await _riot.GetLatestMatchIdAsync(account.puuid, cancellationToken);

            if (!string.IsNullOrEmpty(currentMatchId) && currentMatchId != account.LastMatchId)
            {
                cancellationToken.ThrowIfCancellationRequested();
                
                var matchData = await _riot.GetMatchDetailsAsync(currentMatchId, cancellationToken);
                
                if (matchData != null)
                {
                    _userRegistry.UpdateLastMatchId(discordUserId, currentMatchId);
                    await _onNewMatchFound(account, matchData, cancellationToken);
                    Log.Information("New LoL match detected for {PlayerName}: {MatchId}", account.gameName, currentMatchId);
                }
            }

            if (_onNewTftMatchFound != null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                
                string? currentTftMatchId = await _riot.GetLatestTftMatchIdAsync(account.TftPuuid ?? account.puuid, cancellationToken);

                if (!string.IsNullOrEmpty(currentTftMatchId) && currentTftMatchId != account.LastTftMatchId)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    
                    var tftMatchData = await _riot.GetTftMatchDetailsAsync(currentTftMatchId, cancellationToken);
                    
                    if (tftMatchData != null)
                    {
                        _userRegistry.UpdateLastTftMatchId(discordUserId, currentTftMatchId);
                        await _onNewTftMatchFound(account, tftMatchData, cancellationToken);
                        Log.Information("New TFT match detected for {PlayerName}: {MatchId}", account.gameName, currentTftMatchId);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error checking user {PlayerName}", account.gameName);
        }
    }
}