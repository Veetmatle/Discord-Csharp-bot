using Discord_Bot_AI.Models;
using Discord_Bot_AI.Infrastructure;
using System.Net;
using System.Net.Http.Json;
using Serilog;

namespace Discord_Bot_AI.Services;

/// <summary>
/// Service for interacting with the Riot Games API with built-in rate limiting.
/// Uses IHttpClientFactory for proper HTTP client lifecycle management.
/// Retry policies are configured centrally in ServiceCollectionExtensions.
/// </summary>
public class RiotService : IDisposable
{
    private readonly IHttpClientFactory _httpClientFactory;
    private const string BaseUrl = "https://europe.api.riotgames.com/riot/account/v1/accounts";
    private const int MinRequestIntervalMs = 1200;

    private readonly SemaphoreSlim _rateLimiter = new(1, 1);
    private DateTime _lastRequestTime = DateTime.MinValue;
    private DateTime _backoffUntil = DateTime.MinValue;
    private readonly object _backoffLock = new();

    private readonly SemaphoreSlim _tftRateLimiter = new(1, 1);
    private DateTime _lastTftRequestTime = DateTime.MinValue;
    private DateTime _tftBackoffUntil = DateTime.MinValue;
    private readonly object _tftBackoffLock = new();

    private bool _disposed;

    public RiotService(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }
    
    public bool IsRateLimited
    {
        get
        {
            lock (_backoffLock)
            {
                return DateTime.UtcNow < _backoffUntil;
            }
        }
    }

    public bool IsTftRateLimited
    {
        get
        {
            lock (_tftBackoffLock)
            {
                return DateTime.UtcNow < _tftBackoffUntil;
            }
        }
    }

    /// <summary>
    /// Retrieves Riot Account information based on in-game nickname and tag.
    /// </summary>
    /// <param name="gameNickName">The player's in-game name.</param>
    /// <param name="tag">The player's tag (e.g., EUNE, PL1).</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>The Riot account or null if not found.</returns>
    public async Task<RiotAccount?> GetAccountAsync(string gameNickName, string tag,
        CancellationToken cancellationToken = default)
    {
        var encodedName = Uri.EscapeDataString(gameNickName);
        var encodedTag = Uri.EscapeDataString(tag);
        var url = $"{BaseUrl}/by-riot-id/{encodedName}/{encodedTag}";
        var account = await ExecuteWithRateLimitingAsync<RiotAccount>(url, cancellationToken);
        Log.Information("Account lookup for {GameNickName}#{Tag} returned: {Account}", gameNickName, tag, account != null ? account.puuid : "null");
        
        return account;
    }

    /// <summary>
    /// Retrieves the PUUID for a player using the TFT API key.
    /// Must be stored separately from the League PUUID since Riot encrypts per API key.
    /// </summary>
    public async Task<string?> GetTftAccountPuuidAsync(string gameNickName, string tag,
        CancellationToken cancellationToken = default)
    {
        var encodedName = Uri.EscapeDataString(gameNickName);
        var encodedTag = Uri.EscapeDataString(tag);
        var url = $"{BaseUrl}/by-riot-id/{encodedName}/{encodedTag}";
        var account = await ExecuteWithRateLimitingAsync<RiotAccount>(url, cancellationToken, isTft: true);
        return account?.puuid;
    }

    /// <summary>
    /// Gets the latest match ID for a given player's PUUID.
    /// </summary>
    /// <param name="puuid">The player's unique identifier.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>The latest match ID or null if not found.</returns>
    public async Task<string?> GetLatestMatchIdAsync(string puuid, CancellationToken cancellationToken = default)
    {
        var encodedPuuid = Uri.EscapeDataString(puuid);
        var url = $"https://europe.api.riotgames.com/lol/match/v5/matches/by-puuid/{encodedPuuid}/ids?start=0&count=1";
    
        var matchIds = await ExecuteWithRateLimitingAsync<List<string>>(url, cancellationToken);
        return matchIds?.FirstOrDefault();
    }
    
    public async Task<MatchData?> GetMatchDetailsAsync(string matchId, CancellationToken cancellationToken = default)
    {
        var encodedMatchId = Uri.EscapeDataString(matchId);
        var url = $"https://europe.api.riotgames.com/lol/match/v5/matches/{encodedMatchId}";
        return await ExecuteWithRateLimitingAsync<MatchData>(url, cancellationToken);
    }

    public async Task<string?> GetLatestTftMatchIdAsync(string puuid, CancellationToken cancellationToken = default)
    {
        var encodedPuuid = Uri.EscapeDataString(puuid);
        var url = $"https://europe.api.riotgames.com/tft/match/v1/matches/by-puuid/{encodedPuuid}/ids?start=0&count=1";
        var matchIds = await ExecuteWithRateLimitingAsync<List<string>>(url, cancellationToken, isTft: true);
        return matchIds?.FirstOrDefault();
    }

    public async Task<TftMatchData?> GetTftMatchDetailsAsync(string matchId, CancellationToken cancellationToken = default)
    {
        var encodedMatchId = Uri.EscapeDataString(matchId);
        var url = $"https://europe.api.riotgames.com/tft/match/v1/matches/{encodedMatchId}";
        return await ExecuteWithRateLimitingAsync<TftMatchData>(url, cancellationToken, isTft: true);
    }

    private async Task<T?> ExecuteWithRateLimitingAsync<T>(string url, CancellationToken cancellationToken, bool isTft = false) where T : class
    {
        cancellationToken.ThrowIfCancellationRequested();

        var rateLimiter = isTft ? _tftRateLimiter : _rateLimiter;
        var clientName = isTft ? HttpClientNames.RiotTftApi : HttpClientNames.RiotApi;

        if (isTft ? IsTftRateLimited : IsRateLimited)
        {
            TimeSpan waitTime;
            if (isTft)
            {
                lock (_tftBackoffLock) { waitTime = _tftBackoffUntil - DateTime.UtcNow; }
            }
            else
            {
                lock (_backoffLock) { waitTime = _backoffUntil - DateTime.UtcNow; }
            }
            if (waitTime > TimeSpan.Zero)
            {
                Log.Debug("Rate limited ({Client}). Waiting {WaitTime}s before retry", clientName, waitTime.TotalSeconds);
                await Task.Delay(waitTime, cancellationToken);
            }
        }

        await rateLimiter.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var lastRequestTime = isTft ? _lastTftRequestTime : _lastRequestTime;
            var timeSinceLastRequest = DateTime.UtcNow - lastRequestTime;
            if (timeSinceLastRequest.TotalMilliseconds < MinRequestIntervalMs)
            {
                var delay = MinRequestIntervalMs - (int)timeSinceLastRequest.TotalMilliseconds;
                await Task.Delay(delay, cancellationToken);
            }

            if (isTft) _lastTftRequestTime = DateTime.UtcNow;
            else _lastRequestTime = DateTime.UtcNow;

            var httpClient = _httpClientFactory.CreateClient(clientName);
            var response = await httpClient.GetAsync(url, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<T>(cancellationToken);
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var retryAfter = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(60);
                if (isTft)
                {
                    lock (_tftBackoffLock) { _tftBackoffUntil = DateTime.UtcNow.Add(retryAfter); }
                }
                else
                {
                    lock (_backoffLock) { _backoffUntil = DateTime.UtcNow.Add(retryAfter); }
                }
                Log.Warning("429 received from {Client}. Backing off for {Seconds}s", clientName, retryAfter.TotalSeconds);
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
                return null;

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                Log.Error("{Client} returned Unauthorized (401). Check if the API key is valid and not expired", clientName);
                return null;
            }

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                Log.Error("{Client} returned Forbidden (403). The API key may lack required permissions", clientName);
                return null;
            }

            if (response.StatusCode == HttpStatusCode.BadRequest)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                Log.Error("{Client} returned BadRequest (400). URL: {Url}, Response: {Error}", clientName, url, errorContent);
                return null;
            }

            Log.Warning("{Client} request failed with status {StatusCode}", clientName, response.StatusCode);
            return null;
        }
        catch (OperationCanceledException)
        {
            Log.Debug("Riot API request cancelled");
            throw;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error executing {Client} request to {Url}", clientName, url);
            return null;
        }
        finally
        {
            rateLimiter.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _rateLimiter.Dispose();
        _tftRateLimiter.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}

