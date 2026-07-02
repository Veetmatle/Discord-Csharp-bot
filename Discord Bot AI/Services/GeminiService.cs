using System.Net;
using Discord_Bot_AI.Configuration;
using Discord_Bot_AI.Infrastructure;
using Discord_Bot_AI.Models;
using Mscc.GenerativeAI;
using Mscc.GenerativeAI.Types;
using Serilog;

namespace Discord_Bot_AI.Services;

/// <summary>
/// Service for interacting with Google's Gemini AI API using the Mscc.GenerativeAI library.
/// Supports text prompts, images, and document attachments with rate limiting protection.
/// </summary>
public class GeminiService : IDisposable
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly GenerativeModel _model;
    
    private const string ModelName = "gemini-2.5-flash";
    private readonly string _promptSuffix =
        "\n Answer in Polish in max 100 words. Be brief and precise unless instructions say otherwise.";
    
    private readonly SemaphoreSlim _rateLimiter = new(1, 1);
    private DateTime _lastRequestTime = DateTime.MinValue;
    private const int MinRequestIntervalMs = 1000;
    
    private DateTime _backoffUntil = DateTime.MinValue;
    private readonly object _backoffLock = new();
    
    private bool _disposed;

    public GeminiService(IHttpClientFactory httpClientFactory, AppSettings settings)
    {
        _httpClientFactory = httpClientFactory;
        var googleAi = new GoogleAI(settings.GeminiApiKey);
        _model = googleAi.GenerativeModel(model: ModelName);
    }

    /// <summary>
    /// Checks if the service is currently in a rate-limit backoff state.
    /// </summary>
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

    /// <summary>
    /// Sends a text-only prompt to Gemini API. For backward compatibility.
    /// </summary>
    public Task<string> GetAnswerAsync(string question, CancellationToken cancellationToken = default)
    {
        return GetAnswerAsync(new GeminiRequest { Prompt = question }, cancellationToken);
    }

    /// <summary>
    /// Sends a prompt with optional attachments to Gemini API.
    /// Supports images (PNG, JPEG, GIF, WebP) and documents (PDF, TXT, DOCX, etc.).
    /// </summary>
    public async Task<string> GetAnswerAsync(GeminiRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        
        if (IsRateLimited)
        {
            TimeSpan waitTime;
            lock (_backoffLock)
            {
                waitTime = _backoffUntil - DateTime.UtcNow;
            }
            if (waitTime > TimeSpan.Zero)
            {
                Log.Debug("Rate limited. Waiting {WaitTime}s before retry", waitTime.TotalSeconds);
                await Task.Delay(waitTime, cancellationToken);
            }
        }

        await _rateLimiter.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            
            var timeSinceLastRequest = DateTime.UtcNow - _lastRequestTime;
            if (timeSinceLastRequest.TotalMilliseconds < MinRequestIntervalMs)
            {
                var delay = MinRequestIntervalMs - (int)timeSinceLastRequest.TotalMilliseconds;
                await Task.Delay(delay, cancellationToken);
            }
            
            _lastRequestTime = DateTime.UtcNow;

            var response = await GenerateContentAsync(request, cancellationToken);
            return response;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = TimeSpan.FromSeconds(60);
            lock (_backoffLock)
            {
                _backoffUntil = DateTime.UtcNow.Add(retryAfter);
            }
            Log.Warning("429 received from Gemini API. Backing off for {Seconds}s", retryAfter.TotalSeconds);
            return "Error: Too many requests. Please wait before trying again.";
        }
        catch (OperationCanceledException)
        {
            Log.Debug("Gemini API request cancelled");
            throw;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error executing Gemini API request");
            return $"Error: {ex.Message}";
        }
        finally
        {
            _rateLimiter.Release();
        }
    }

    private async Task<string> GenerateContentAsync(GeminiRequest request, CancellationToken cancellationToken)
    {
        var fullPrompt = request.Prompt + _promptSuffix;
        
        if (request.Attachments.Count == 0)
        {
            var textResponse = await _model.GenerateContent(fullPrompt, cancellationToken: cancellationToken);
            return ExtractTextFromResponse(textResponse);
        }
        
        var parts = new List<IPart>();
        parts.Add(new TextData { Text = fullPrompt });
        
        using var httpClient = _httpClientFactory.CreateClient(HttpClientNames.GeminiApi);
        
        foreach (var attachment in request.Attachments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            
            if (!GeminiSupportedTypes.IsSupported(attachment.MimeType))
            {
                Log.Warning("Unsupported attachment type: {MimeType}, skipping {FileName}", 
                    attachment.MimeType, attachment.FileName);
                continue;
            }
            
            if (attachment.Size > GeminiSupportedTypes.MaxFileSizeBytes)
            {
                Log.Warning("Attachment too large: {Size} bytes, max {Max} bytes, skipping {FileName}",
                    attachment.Size, GeminiSupportedTypes.MaxFileSizeBytes, attachment.FileName);
                continue;
            }
            
            try
            {
                var fileBytes = await httpClient.GetByteArrayAsync(attachment.Url, cancellationToken);
                
                var inlineDataPart = new InlineData
                {
                    MimeType = attachment.MimeType,
                    Data = Convert.ToBase64String(fileBytes)
                };
                parts.Add(inlineDataPart);
                
                Log.Debug("Added attachment: {FileName} ({MimeType}, {Size} bytes)", 
                    attachment.FileName, attachment.MimeType, fileBytes.Length);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to download attachment {FileName} from {Url}", 
                    attachment.FileName, attachment.Url);
            }
        }
        
        var response = await _model.GenerateContent(parts, cancellationToken: cancellationToken);
        
        return ExtractTextFromResponse(response);
    }

    private static string ExtractTextFromResponse(GenerateContentResponse? response)
    {
        if (response?.Candidates == null || response.Candidates.Count == 0)
            return "No answer found.";
            
        var firstCandidate = response.Candidates.FirstOrDefault();
        if (firstCandidate?.Content?.Parts == null)
            return "No answer found.";
            
        var textParts = firstCandidate.Content.Parts
            .Where(p => !string.IsNullOrEmpty(p.Text))
            .Select(p => p.Text);
            
        var combinedText = string.Join("\n", textParts.ToList());
        
        return string.IsNullOrEmpty(combinedText) ? "No answer found." : combinedText;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _rateLimiter.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}

