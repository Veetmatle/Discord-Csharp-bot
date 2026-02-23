using System.Text.Json;
using System.Text.RegularExpressions;
using Serilog;

namespace Discord_Bot_AI.Services;

/// <summary>
/// Background service that monitors Politechnika Krakowska WIiT website for new/updated
/// schedules and documents. Supports per-channel subscriptions - notifications are sent
/// only to channels where the watcher was started.
/// </summary>
public class PolitechnikaWatcherService : IDisposable
{
    private readonly PolitechnikaService _politechnikaService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _statePath;
    
    private readonly SemaphoreSlim _stateLock = new(1, 1);
    private readonly ReaderWriterLockSlim _fileLock = new();
    
    private CancellationTokenSource? _watcherCts;
    private Task? _watcherTask;
    private bool _isRunning;
    private bool _disposed;
    
    private WatcherState _state = new();
    
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(30);
    
    private static readonly WatchPattern[] WatchPatterns =
    {
        new("Informatyka I stopień - plan zajęć", 
            new[] { "Kierunek", "informatyka", "I stop" },
            new[] { "matematyka", "stosowana" })
    };

    public PolitechnikaWatcherService(PolitechnikaService politechnikaService, IHttpClientFactory httpClientFactory, string dataPath)
    {
        _politechnikaService = politechnikaService;
        _httpClientFactory = httpClientFactory;
        _statePath = Path.Combine(dataPath, "pk_state.json");
    }

    public Func<PolitechnikaChangeNotification, CancellationToken, Task>? OnChangeDetected { get; set; }
    
    public bool IsRunning => _isRunning;
    
    /// <summary>
    /// Returns a snapshot of current subscriptions. Thread-safe.
    /// </summary>
    public async Task<List<WatcherSubscription>> GetSubscriptionsAsync(CancellationToken ct = default)
    {
        await _stateLock.WaitAsync(ct);
        try { return _state.Subscriptions.ToList(); }
        finally { _stateLock.Release(); }
    }

    public async Task<bool> SubscribeChannelAsync(ulong guildId, ulong channelId, ulong startedByUserId, CancellationToken ct = default)
    {
        await _stateLock.WaitAsync(ct);
        try
        {
            if (_state.Subscriptions.Any(s => s.GuildId == guildId && s.ChannelId == channelId))
                return false;
            
            _state.Subscriptions.Add(new WatcherSubscription
            {
                GuildId = guildId,
                ChannelId = channelId,
                StartedByUserId = startedByUserId,
                StartedAt = DateTime.UtcNow
            });
            
            await SaveStateInternalAsync(ct);
            Log.Information("Channel {ChannelId} subscribed to PK watcher", channelId);
            return true;
        }
        finally { _stateLock.Release(); }
    }

    public async Task<bool> UnsubscribeChannelAsync(ulong guildId, ulong channelId, CancellationToken ct = default)
    {
        await _stateLock.WaitAsync(ct);
        try
        {
            var sub = _state.Subscriptions.FirstOrDefault(s => s.GuildId == guildId && s.ChannelId == channelId);
            if (sub == null) return false;
            
            _state.Subscriptions.Remove(sub);
            await SaveStateInternalAsync(ct);
            Log.Information("Channel {ChannelId} unsubscribed from PK watcher", channelId);
            return true;
        }
        finally { _stateLock.Release(); }
    }

    /// <summary>
    /// Checks if a specific channel is subscribed. Thread-safe.
    /// </summary>
    public async Task<bool> IsChannelSubscribedAsync(ulong guildId, ulong channelId, CancellationToken ct = default)
    {
        await _stateLock.WaitAsync(ct);
        try { return _state.Subscriptions.Any(s => s.GuildId == guildId && s.ChannelId == channelId); }
        finally { _stateLock.Release(); }
    }

    public async Task StartWatchingAsync(CancellationToken externalToken = default)
    {
        if (_isRunning) return;

        await LoadStateAsync(externalToken);
        _watcherCts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
        _watcherTask = RunWatchLoopAsync(_watcherCts.Token);
        _isRunning = true;
        
        Log.Information("PK watcher started. Interval: {Interval}min, {SubCount} subscription(s)", 
            CheckInterval.TotalMinutes, _state.Subscriptions.Count);
    }

    public async Task StopWatchingAsync()
    {
        if (!_isRunning || _watcherCts == null || _watcherTask == null) return;

        _watcherCts.Cancel();
        try { await _watcherTask.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (OperationCanceledException) { }
        catch (TimeoutException) { Log.Warning("PK watcher did not stop gracefully"); }
        
        _isRunning = false;
        Log.Information("PK watcher stopped");
    }

    private async Task RunWatchLoopAsync(CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromMinutes(1), ct);
        
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_state.Subscriptions.Count > 0)
                    await CheckForChangesAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Log.Error(ex, "Error in PK watcher loop"); }

            try { await Task.Delay(CheckInterval, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task CheckForChangesAsync(CancellationToken ct)
    {
        Log.Debug("Checking PK website for changes...");
        
        var currentLinks = await _politechnikaService.GetScrapedLinksAsync(forceRefresh: true, ct);
        if (currentLinks.Count == 0) { Log.Warning("No links scraped"); return; }

        var changes = new List<DetectedChange>();
        var isFirstRun = _state.KnownLinks.Count == 0;

        foreach (var pattern in WatchPatterns)
        {
            foreach (var link in FindMatchingLinks(currentLinks, pattern))
            {
                var linkText = link.Text;
                var linkUrl = link.Url;
                var currentDate = ExtractDateFromText(linkText);
                var linkKey = NormalizeLinkKey(linkUrl);
                
                if (_state.KnownLinks.TryGetValue(linkKey, out var prev))
                {
                    var dateChanged = currentDate != null && prev.ExtractedDate != currentDate;
                    var textChanged = !string.Equals(prev.LinkText, linkText, StringComparison.OrdinalIgnoreCase);
                    var newDateAppeared = prev.ExtractedDate == null && currentDate != null;
                    
                    var fileModified = false;
                    if (!dateChanged && !newDateAppeared && IsFileLink(linkUrl))
                    {
                        var lastModified = await GetLastModifiedAsync(linkUrl, ct);
                        if (lastModified != null && prev.LastModifiedHeader != null
                            && lastModified != prev.LastModifiedHeader)
                        {
                            fileModified = true;
                            Log.Information("FILE MODIFIED on server: {Pattern} Last-Modified {Old} -> {New}",
                                pattern.Name, prev.LastModifiedHeader, lastModified);
                        }
                        _state.KnownLinks[linkKey] = new LinkInfo
                        {
                            LinkText = linkText, LinkUrl = linkUrl,
                            ExtractedDate = currentDate, LastSeen = DateTime.UtcNow,
                            LastModifiedHeader = lastModified ?? prev.LastModifiedHeader
                        };
                    }
                    
                    if (dateChanged || (textChanged && newDateAppeared) || fileModified)
                    {
                        changes.Add(new DetectedChange
                        {
                            ChangeType = ChangeType.Updated, PatternName = pattern.Name,
                            LinkText = linkText, LinkUrl = linkUrl,
                            OldDate = prev.ExtractedDate, NewDate = currentDate
                        });
                        Log.Information("UPDATE: {Pattern} date {Old} -> {New} (text: {TextChanged}, file: {FileModified})",
                            pattern.Name, prev.ExtractedDate ?? "null", currentDate ?? "null", textChanged, fileModified);
                    }
                }
                else if (!isFirstRun)
                {
                    changes.Add(new DetectedChange
                    {
                        ChangeType = ChangeType.New, PatternName = pattern.Name,
                        LinkText = linkText, LinkUrl = linkUrl, NewDate = currentDate
                    });
                    Log.Information("NEW: {Pattern} - {Link}", pattern.Name, linkText);
                }
                else
                {
                    Log.Debug("First run - cataloging link: {Link}", linkText);
                }

                if (!_state.KnownLinks.ContainsKey(linkKey) || _state.KnownLinks[linkKey].LinkText != linkText)
                {
                    var lastMod = IsFileLink(linkUrl) ? await GetLastModifiedAsync(linkUrl, ct) : null;
                    _state.KnownLinks[linkKey] = new LinkInfo
                    {
                        LinkText = linkText, LinkUrl = linkUrl,
                        ExtractedDate = currentDate, LastSeen = DateTime.UtcNow,
                        LastModifiedHeader = lastMod ?? _state.KnownLinks.GetValueOrDefault(linkKey)?.LastModifiedHeader
                    };
                }
                else
                {
                    _state.KnownLinks[linkKey].LastSeen = DateTime.UtcNow;
                    _state.KnownLinks[linkKey].ExtractedDate = currentDate;
                }
            }
        }

        _state.LastCheck = DateTime.UtcNow;
        await SaveStateAsync(ct);

        if (changes.Count > 0 && OnChangeDetected != null && _state.Subscriptions.Count > 0)
        {
            foreach (var change in changes)
            {
                try
                {
                    await OnChangeDetected(new PolitechnikaChangeNotification
                    {
                        ChangeType = change.ChangeType, PatternName = change.PatternName,
                        LinkText = change.LinkText, LinkUrl = change.LinkUrl,
                        OldDate = change.OldDate, NewDate = change.NewDate,
                        SubscribedChannels = _state.Subscriptions.ToList()
                    }, ct);
                }
                catch (Exception ex) { Log.Error(ex, "Failed to send PK notification"); }
            }
        }
    }

    /// <summary>
    /// Checks if a URL points to a downloadable document file.
    /// </summary>
    private static bool IsFileLink(string url)
    {
        var extensions = new[] { ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx" };
        return extensions.Any(ext => url.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Fetches the Last-Modified header from a URL using a HEAD request.
    /// Returns the value as string for comparison, or null if unavailable.
    /// </summary>
    private async Task<string?> GetLastModifiedAsync(string url, CancellationToken ct)
    {
        try
        {
            using var httpClient = _httpClientFactory.CreateClient();
            httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
            httpClient.Timeout = TimeSpan.FromSeconds(10);
            
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            
            if (response.Content.Headers.LastModified.HasValue)
            {
                return response.Content.Headers.LastModified.Value.ToString("O");
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not fetch Last-Modified for {Url}", url);
        }
        return null;
    }

    /// <summary>
    /// Finds links matching a watch pattern from the scraped link list.
    /// </summary>
    private static List<ScrapedLink> FindMatchingLinks(List<ScrapedLink> links, WatchPattern pattern)
    {
        return links.Where(l =>
        {
            var combined = (l.Text + " " + l.Url).ToLowerInvariant();
            return pattern.IncludeKeywords.All(k => combined.Contains(k)) &&
                   !pattern.ExcludeKeywords.Any(k => combined.Contains(k));
        }).ToList();
    }

    /// <summary>
    /// Extracts date or version information from link text.
    /// Supports multiple date formats commonly used on PK website.
    /// </summary>
    private static string? ExtractDateFromText(string text)
    {
        var lowerText = text.ToLowerInvariant();
        
        string[] patterns = 
        { 
            @"(\d{1,2})[-./](\d{1,2})[-./](\d{4})",
            @"(\d{4})[-./](\d{1,2})[-./](\d{1,2})",  
            @"(\d{1,2})[-./](\d{1,2})(?!\d)",      
            @"aktualiz\w*[\s:]*(\d{1,2})[-./](\d{1,2})",  
            @"z\s+dnia\s+(\d{1,2})[-./](\d{1,2})",        
            @"semestr\s*(letni|zimowy)\s*(\d{4})",        
            @"(\d{4})[/](\d{4})",                          
        };
        
        foreach (var p in patterns)
        {
            var m = Regex.Match(lowerText, p, RegexOptions.IgnoreCase);
            if (m.Success) return m.Value;
        }
        
        var v = Regex.Match(lowerText, @"v\.?\s*(\d+)", RegexOptions.IgnoreCase);
        if (v.Success) return $"v{v.Groups[1].Value}";
        
        var urlDateMatch = Regex.Match(text, @"(\d{4})(\d{2})(\d{2})", RegexOptions.None);
        if (urlDateMatch.Success) return urlDateMatch.Value;
        
        return null;
    }

    private static string NormalizeLinkKey(string url) => new Uri(url).GetLeftPart(UriPartial.Path).ToLowerInvariant();

    private async Task LoadStateAsync(CancellationToken ct)
    {
        await _stateLock.WaitAsync(ct);
        try
        {
            _fileLock.EnterReadLock();
            try
            {
                if (!File.Exists(_statePath)) { _state = new WatcherState(); return; }
                var json = await File.ReadAllTextAsync(_statePath, ct);
                _state = JsonSerializer.Deserialize<WatcherState>(json) ?? new WatcherState();
            }
            finally { _fileLock.ExitReadLock(); }
        }
        catch { _state = new WatcherState(); }
        finally { _stateLock.Release(); }
    }

    private async Task SaveStateAsync(CancellationToken ct)
    {
        await _stateLock.WaitAsync(ct);
        try { await SaveStateInternalAsync(ct); }
        finally { _stateLock.Release(); }
    }
    
    private async Task SaveStateInternalAsync(CancellationToken ct)
    {
        _fileLock.EnterWriteLock();
        try
        {
            var dir = Path.GetDirectoryName(_statePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(_state, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(_statePath, json, ct);
        }
        finally { _fileLock.ExitWriteLock(); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _watcherCts?.Cancel();
        _watcherCts?.Dispose();
        _stateLock.Dispose();
        _fileLock.Dispose();
        _disposed = true;
    }
}

public record WatchPattern(string Name, string[] IncludeKeywords, string[] ExcludeKeywords);
public enum ChangeType { New, Updated }

public class WatcherSubscription
{
    public ulong GuildId { get; set; }
    public ulong ChannelId { get; set; }
    public ulong StartedByUserId { get; set; }
    public DateTime StartedAt { get; set; }
}

internal class DetectedChange
{
    public ChangeType ChangeType { get; set; }
    public string PatternName { get; set; } = "";
    public string LinkText { get; set; } = "";
    public string LinkUrl { get; set; } = "";
    public string? OldDate { get; set; }
    public string? NewDate { get; set; }
}

public class PolitechnikaChangeNotification
{
    public ChangeType ChangeType { get; set; }
    public string PatternName { get; set; } = "";
    public string LinkText { get; set; } = "";
    public string LinkUrl { get; set; } = "";
    public string? OldDate { get; set; }
    public string? NewDate { get; set; }
    public List<WatcherSubscription> SubscribedChannels { get; set; } = new();
}

public class WatcherState
{
    public DateTime LastCheck { get; set; }
    public List<WatcherSubscription> Subscriptions { get; set; } = new();
    public Dictionary<string, LinkInfo> KnownLinks { get; set; } = new();
}

public class LinkInfo
{
    public string LinkText { get; set; } = "";
    public string LinkUrl { get; set; } = "";
    public string? ExtractedDate { get; set; }
    public DateTime LastSeen { get; set; }
    public string? LastModifiedHeader { get; set; }
}
