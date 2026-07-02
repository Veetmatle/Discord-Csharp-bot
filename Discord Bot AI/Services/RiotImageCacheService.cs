using System.Collections.Concurrent;
using System.Text.Json;
using Discord_Bot_AI.Models;
using Serilog;

namespace Discord_Bot_AI.Services;

/// <summary>
/// Provides a thread-safe caching mechanism for Riot Games' Data Dragon image assets.
/// Supports automatic version detection and fallback to Community Dragon for missing assets.
/// </summary>
public class RiotImageCacheService : IDisposable
{
    private readonly HttpClient _httpClient = new();
    private readonly string _cachePath;
    private string _version;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _downloadLocks = new();
    private readonly ConcurrentDictionary<string, bool> _cachedFiles = new();
    private readonly ConcurrentDictionary<int, bool> _invalidItemIds = new();
    private bool _disposed;
    private DateTime _lastVersionCheck = DateTime.MinValue;
    private static readonly TimeSpan VersionCheckInterval = TimeSpan.FromHours(24);

    private const string DataDragonVersionUrl = "https://ddragon.leagueoflegends.com/api/versions.json";
    private const string CommunityDragonBaseUrl = "https://raw.communitydragon.org/latest/plugins/rcp-be-lol-game-data/global/default/assets/items/icons2d";

    /// <summary>
    /// Initializes the cache service with a specific game version and cache path.
    /// </summary>
    /// <param name="version">The Riot Data Dragon version string (e.g., "14.2.1"). If null or "latest", fetches automatically.</param>
    /// <param name="cachePath">The directory path for caching images.</param>
    public RiotImageCacheService(string version, string cachePath = "Assets/Cache")
    {
        _version = version;
        _cachePath = cachePath;
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36");
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
        
        Directory.CreateDirectory(_cachePath);
        Directory.CreateDirectory(Path.Combine(_cachePath, "champions"));
        Directory.CreateDirectory(Path.Combine(_cachePath, "items"));
        
        MigrateLegacyNames();
        ScanExistingCache();
        
        _ = Task.Run(async () => await EnsureLatestVersionAsync());
        
        Log.Information("RiotImageCacheService initialized with cache path: {CachePath}", cachePath);
    }
    
    /// <summary>
    /// Fetches and updates to the latest Data Dragon version if needed.
    /// </summary>
    private async Task EnsureLatestVersionAsync()
    {
        if (DateTime.UtcNow - _lastVersionCheck < VersionCheckInterval) return;
        
        try
        {
            var response = await _httpClient.GetStringAsync(DataDragonVersionUrl);
            var versions = JsonSerializer.Deserialize<List<string>>(response);
            
            if (versions != null && versions.Count > 0)
            {
                var latestVersion = versions[0];
                if (_version != latestVersion)
                {
                    Log.Information("Updating Data Dragon version from {OldVersion} to {NewVersion}", _version, latestVersion);
                    _version = latestVersion;
                }
                _lastVersionCheck = DateTime.UtcNow;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to fetch latest Data Dragon version, using: {Version}", _version);
        }
    }
    
    private string NormalizeChampionName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return name;
        string cleanName = name.Replace("'", "").Replace(" ", "");
        
        return cleanName.ToLower() switch
        {
            "wukong" or "monkeyking" => "MonkeyKing",
            "reksai"      => "RekSai",
            "drmundo"     => "DrMundo",
            "leesin"      => "LeeSin",
            "masteryi"    => "MasterYi",
            "missfortune" => "MissFortune",
            "jarvaniv"    => "JarvanIV",
            "tahmkench"   => "TahmKench",
            "twistedfate" => "TwistedFate",
            "xinzhao"     => "XinZhao",
            "aurelionsol" => "AurelionSol",
            "kogmaw"      => "KogMaw",
            "ksante"      => "KSante",
            "fiddlesticks" => "Fiddlesticks",
            "leblanc"      => "Leblanc",
            "belveth"      => "Belveth",
            "velkoz"       => "Velkoz",
            "khazix"       => "Khazix",
            "kaisa"        => "Kaisa",
            "nunu"         => "Nunu",
            _ => char.ToUpper(cleanName[0]) + cleanName.Substring(1).ToLower()
        };
    }
    
    
    /// <summary>
    /// Scans the cache directory on startup to populate the in-memory cache index.
    /// </summary>
    private void ScanExistingCache()
    {
        try
        {
            foreach (var file in Directory.GetFiles(Path.Combine(_cachePath, "champions")))
            {
                _cachedFiles[file] = true;
            }
            foreach (var file in Directory.GetFiles(Path.Combine(_cachePath, "items")))
            {
                _cachedFiles[file] = true;
            }
            Log.Debug("Cache scanned: {Count} files found", _cachedFiles.Count);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to scan cache directory");
        }
    }
    
    private void MigrateLegacyNames()
    {
        var champDir = Path.Combine(_cachePath, "champions");
        foreach (var file in Directory.GetFiles(champDir, "*.png"))
        {
            try
            {
                var name = Path.GetFileNameWithoutExtension(file);
                var normalized = NormalizeChampionName(name);
                if (normalized != name)
                {
                    var newPath = Path.Combine(champDir, $"{normalized}.png");
                    if (!File.Exists(newPath))
                        File.Move(file, newPath);
                }
            }
            catch (Exception e)
            {
                Log.Warning(e, "Failed to migrate champion names: {File}: {Msg}", file, e.Message);
            }
        }
    }

    /// <summary>
    /// Retrieves the local file path for a champion's icon, downloading it from Data Dragon if it is not already present in the cache.
    /// </summary>
    /// <param name="championName">The internal name of the champion.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>The full local path to the champion icon image.</returns>
    public async Task<string> GetChampionIconAsync(string championName, CancellationToken cancellationToken = default)
    {
        var safeName = NormalizeChampionName(championName);
        string fileName = $"{safeName}.png";
        string localPath = Path.Combine(_cachePath, "champions", fileName);

        if (File.Exists(localPath)) return localPath;
        
        await EnsureLatestVersionAsync();

        string url = $"https://ddragon.leagueoflegends.com/cdn/{_version}/img/champion/{fileName}";
        var result = await DownloadImageAsync(url, localPath, cancellationToken);

        return !string.IsNullOrEmpty(result) ? result : localPath;
    }

    /// <summary>
    /// Retrieves the local file path for an item's icon based on its ID, downloading it if necessary. 
    /// Returns an empty string if the item ID is 0 (empty slot) or if the item doesn't exist in Data Dragon.
    /// Falls back to Community Dragon for missing assets.
    /// </summary>
    /// <param name="itemId">The unique ID of the item.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>The local path to the item icon image, or an empty string if unavailable.</returns>
    public async Task<string> GetItemIconAsync(int itemId, CancellationToken cancellationToken = default)
    {
        if (itemId == 0) return string.Empty;
        
        if (_invalidItemIds.ContainsKey(itemId)) return string.Empty;

        string fileName = $"{itemId}.png";
        string localPath = Path.Combine(_cachePath, "items", fileName);

        if (_cachedFiles.ContainsKey(localPath) || File.Exists(localPath))
        {
            _cachedFiles[localPath] = true;
            return localPath;
        }
        
        await EnsureLatestVersionAsync();
        
        string ddUrl = $"https://ddragon.leagueoflegends.com/cdn/{_version}/img/item/{fileName}";
        var result = await DownloadImageWithFallbackAsync(ddUrl, localPath, itemId, cancellationToken);
        
        if (!string.IsNullOrEmpty(result)) return result;
        
        // Fallback 
        string cdUrl = $"{CommunityDragonBaseUrl}/{itemId}_class_{itemId}_inventoryicon.png";
        result = await DownloadImageAsync(cdUrl, localPath, cancellationToken, suppressWarning: true);
        
        if (!string.IsNullOrEmpty(result)) return result;
        
        // Alternative CDragon path 
        string cdUrlAlt = $"https://raw.communitydragon.org/latest/game/assets/items/icons2d/{itemId.ToString().ToLower()}_class_{itemId.ToString().ToLower()}_inventoryicon.png";
        result = await DownloadImageAsync(cdUrlAlt, localPath, cancellationToken, suppressWarning: true);
        
        if (string.IsNullOrEmpty(result))
        {
            _invalidItemIds[itemId] = true;
            Log.Debug("Item {ItemId} not found in Data Dragon or Community Dragon, marking as invalid", itemId);
        }

        return result;
    }

    /// <summary>
    /// Performs the actual HTTP download of the image data and saves it to the specified local destination.
    /// Uses per-file locking to prevent concurrent downloads of the same file.
    /// </summary>
    /// <param name="url">The remote Data Dragon URL to download from.</param>
    /// <param name="destination">The local file path where the image should be saved.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <param name="suppressWarning">If true, does not log warnings for 403/404 errors.</param>
    /// <returns>The destination path if successful, empty string otherwise.</returns>
    private async Task<string> DownloadImageAsync(string url, string destination, CancellationToken cancellationToken, bool suppressWarning = false)
    {
        var lockObj = _downloadLocks.GetOrAdd(destination, _ => new SemaphoreSlim(1, 1));
        
        await lockObj.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            
            if (_cachedFiles.ContainsKey(destination) || File.Exists(destination))
            {
                _cachedFiles[destination] = true;
                return destination;
            }

            using var response = await _httpClient.GetAsync(url, cancellationToken);
            
            if (!response.IsSuccessStatusCode)
            {
                if (!suppressWarning && response.StatusCode != System.Net.HttpStatusCode.NotFound && 
                    response.StatusCode != System.Net.HttpStatusCode.Forbidden)
                {
                    Log.Warning("Failed to download image {Url}: {StatusCode}", url, response.StatusCode);
                }
                return string.Empty;
            }
            
            var data = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            
            var tempPath = destination + ".tmp";
            await File.WriteAllBytesAsync(tempPath, data, cancellationToken);
            File.Move(tempPath, destination, overwrite: true);
            
            _cachedFiles[destination] = true;
            Log.Debug("Downloaded and cached: {Destination}", destination);
            return destination;
        }
        catch (OperationCanceledException)
        {
            Log.Debug("Image download cancelled: {Url}", url);
            throw;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Forbidden ||
                                               ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Silently ignore 403/404 - item doesn't exist in this version
            return string.Empty;
        }
        catch (Exception ex)
        {
            if (!suppressWarning)
            {
                Log.Warning(ex, "Error downloading image {Url}", url);
            }
            return string.Empty;
        }
        finally
        {
            lockObj.Release();
        }
    }
    
    /// <summary>
    /// Attempts to download from Data Dragon, returns empty string on 403/404 to allow fallback.
    /// </summary>
    private async Task<string> DownloadImageWithFallbackAsync(string url, string destination, int itemId, CancellationToken cancellationToken)
    {
        var lockObj = _downloadLocks.GetOrAdd(destination, _ => new SemaphoreSlim(1, 1));
        
        await lockObj.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            
            if (_cachedFiles.ContainsKey(destination) || File.Exists(destination))
            {
                _cachedFiles[destination] = true;
                return destination;
            }

            using var response = await _httpClient.GetAsync(url, cancellationToken);
            
            if (response.StatusCode == System.Net.HttpStatusCode.Forbidden ||
                response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // Item doesn't exist in Data Dragon, will try Community Dragon
                Log.Debug("Item {ItemId} not found in Data Dragon (status: {Status}), trying Community Dragon", 
                    itemId, response.StatusCode);
                return string.Empty;
            }
            
            if (!response.IsSuccessStatusCode)
            {
                Log.Warning("Failed to download item {ItemId}: {StatusCode}", itemId, response.StatusCode);
                return string.Empty;
            }
            
            var data = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            
            var tempPath = destination + ".tmp";
            await File.WriteAllBytesAsync(tempPath, data, cancellationToken);
            File.Move(tempPath, destination, overwrite: true);
            
            _cachedFiles[destination] = true;
            Log.Debug("Downloaded and cached item {ItemId} from Data Dragon", itemId);
            return destination;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Forbidden ||
                                               ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return string.Empty;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Error downloading item {ItemId} from {Url}", itemId, url);
            return string.Empty;
        }
        finally
        {
            lockObj.Release();
        }
    }

    /// <summary>
    /// Returns statistics about the current cache state.
    /// </summary>
    public CacheStats GetCacheStats()
    {
        try
        {
            var champPath = Path.Combine(_cachePath, "champions");
            var itemsPath = Path.Combine(_cachePath, "items");
            
            var champFiles = Directory.Exists(champPath) ? Directory.GetFiles(champPath) : Array.Empty<string>();
            var itemFiles = Directory.Exists(itemsPath) ? Directory.GetFiles(itemsPath) : Array.Empty<string>();
            
            long totalSize = 0;
            foreach (var file in champFiles.Concat(itemFiles))
            {
                try
                {
                    totalSize += new FileInfo(file).Length;
                }
                catch
                {
                    // Ignore
                }
            }
            
            return new CacheStats
            {
                FileCount = champFiles.Length + itemFiles.Length,
                TotalSizeMB = totalSize / (1024.0 * 1024.0)
            };
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to get cache stats");
            return new CacheStats { FileCount = 0, TotalSizeMB = 0 };
        }
    }

    /// <summary>
    /// Removes cached files older than the specified age to prevent disk bloat.
    /// Should be called periodically (e.g., once per week).
    /// </summary>
    /// <param name="maxAge">Maximum age of files to keep.</param>
    /// <returns>Number of files deleted.</returns>
    public int CleanupOldFiles(TimeSpan maxAge)
    {
        int deletedCount = 0;
        var cutoffTime = DateTime.UtcNow - maxAge;
        
        try
        {
            var directories = new[]
            {
                Path.Combine(_cachePath, "champions"),
                Path.Combine(_cachePath, "items")
            };

            foreach (var dir in directories)
            {
                if (!Directory.Exists(dir)) continue;
                
                foreach (var file in Directory.GetFiles(dir))
                {
                    try
                    {
                        var fileInfo = new FileInfo(file);
                        if (fileInfo.LastAccessTimeUtc < cutoffTime)
                        {
                            fileInfo.Delete();
                            _cachedFiles.TryRemove(file, out _);
                            deletedCount++;
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "Failed to delete cache file: {File}", file);
                    }
                }
            }
            
            if (deletedCount > 0)
            {
                Log.Information("Cache cleanup: deleted {Count} old files", deletedCount);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Cache cleanup failed");
        }
        
        return deletedCount;
    }
    
    public void Dispose()
    {
        if (_disposed) return;
        
        _httpClient.Dispose();
        foreach (var lockObj in _downloadLocks.Values)
        {
            lockObj.Dispose();
        }
        _downloadLocks.Clear();
        
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}