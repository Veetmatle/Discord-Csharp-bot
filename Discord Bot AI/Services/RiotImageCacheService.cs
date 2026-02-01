using System.Collections.Concurrent;
using Discord_Bot_AI.Models;
using Serilog;

namespace Discord_Bot_AI.Services;

/// <summary>
/// Provides a thread-safe caching mechanism for Riot Games' Data Dragon image assets.
/// </summary>
public class RiotImageCacheService : IDisposable
{
    private readonly HttpClient _httpClient = new();
    private readonly string _cachePath;
    private readonly string _version;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _downloadLocks = new();
    private readonly ConcurrentDictionary<string, bool> _cachedFiles = new();
    private bool _disposed;

    /// <summary>
    /// Initializes the cache service with a specific game version and cache path.
    /// </summary>
    /// <param name="version">The Riot Data Dragon version string (e.g., "14.2.1").</param>
    /// <param name="cachePath">The directory path for caching images.</param>
    public RiotImageCacheService(string version, string cachePath = "Assets/Cache")
    {
        _version = version;
        _cachePath = cachePath;
        
        Directory.CreateDirectory(_cachePath);
        Directory.CreateDirectory(Path.Combine(_cachePath, "champions"));
        Directory.CreateDirectory(Path.Combine(_cachePath, "items"));
        
        ScanExistingCache();
        Log.Information("RiotImageCacheService initialized with cache path: {CachePath}", cachePath);
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

    /// <summary>
    /// Retrieves the local file path for a champion's icon, downloading it from Data Dragon if it is not already present in the cache.
    /// </summary>
    /// <param name="championName">The internal name of the champion.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>The full local path to the champion icon image.</returns>
    public async Task<string> GetChampionIconAsync(string championName, CancellationToken cancellationToken = default)
    {
        string fileName = $"{championName}.png";
        string localPath = Path.Combine(_cachePath, "champions", fileName);

        if (_cachedFiles.ContainsKey(localPath) || File.Exists(localPath))
        {
            _cachedFiles[localPath] = true;
            return localPath;
        }

        string url = $"https://ddragon.riotgames.com/cdn/{_version}/img/champion/{fileName}";
        await DownloadImageAsync(url, localPath, cancellationToken);

        return localPath;
    }

    /// <summary>
    /// Retrieves the local file path for an item's icon based on its ID, downloading it if necessary. 
    /// Returns an empty string if the item ID is 0 (empty slot).
    /// </summary>
    /// <param name="itemId">The unique ID of the item.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>The local path to the item icon image, or an empty string if the ID is 0.</returns>
    public async Task<string> GetItemIconAsync(int itemId, CancellationToken cancellationToken = default)
    {
        if (itemId == 0) return string.Empty;

        string fileName = $"{itemId}.png";
        string localPath = Path.Combine(_cachePath, "items", fileName);

        if (_cachedFiles.ContainsKey(localPath) || File.Exists(localPath))
        {
            _cachedFiles[localPath] = true;
            return localPath;
        }

        string url = $"https://ddragon.riotgames.com/cdn/{_version}/img/item/{fileName}";
        await DownloadImageAsync(url, localPath, cancellationToken);

        return localPath;
    }

    /// <summary>
    /// Performs the actual HTTP download of the image data and saves it to the specified local destination.
    /// Uses per-file locking to prevent concurrent downloads of the same file.
    /// </summary>
    /// <param name="url">The remote Data Dragon URL to download from.</param>
    /// <param name="destination">The local file path where the image should be saved.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    private async Task DownloadImageAsync(string url, string destination, CancellationToken cancellationToken)
    {
        var lockObj = _downloadLocks.GetOrAdd(destination, _ => new SemaphoreSlim(1, 1));
        
        await lockObj.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            
            if (_cachedFiles.ContainsKey(destination) || File.Exists(destination))
            {
                _cachedFiles[destination] = true;
                return;
            }

            var data = await _httpClient.GetByteArrayAsync(url, cancellationToken);
            
            var tempPath = destination + ".tmp";
            await File.WriteAllBytesAsync(tempPath, data, cancellationToken);
            File.Move(tempPath, destination, overwrite: true);
            
            _cachedFiles[destination] = true;
            Log.Debug("Downloaded and cached: {Destination}", destination);
        }
        catch (OperationCanceledException)
        {
            Log.Debug("Image download cancelled: {Url}", url);
            throw;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Error downloading image {Url}", url);
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
                    // Ignore files that can't be accessed
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

    /// <summary>
    /// Releases resources used by the cache service.
    /// </summary>
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