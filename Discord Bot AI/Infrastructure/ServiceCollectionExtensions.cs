using System.Net;
using Discord_Bot_AI.Configuration;
using Discord_Bot_AI.Data;
using Discord_Bot_AI.Services;
using Discord_Bot_AI.Strategy.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Polly;
using Polly.Extensions.Http;
using Serilog;

namespace Discord_Bot_AI.Infrastructure;

/// <summary>
/// Extension methods for configuring application services with dependency injection.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers all application services including HTTP clients with Polly retry policies.
    /// </summary>
    public static IServiceCollection AddApplicationServices(this IServiceCollection services, AppSettings settings)
    {
        // Register settings as singleton
        services.AddSingleton(settings);
        
        // Register HTTP clients with named policies
        services.AddRiotHttpClient(settings);
        services.AddGeminiHttpClient();
        
        // Register core services
        services.AddSingleton<IUserRegistry>(_ => new UserRegistry(settings.DataPath));
        services.AddSingleton<IGuildConfigRegistry>(_ => new GuildConfigRegistry(settings.DataPath));
        services.AddSingleton<RiotImageCacheService>(_ => new RiotImageCacheService(settings.RiotVersion, settings.CachePath));
        services.AddSingleton<IGameSummaryRenderer, ImageSharpRenderer>();
        
        // Register API services that use IHttpClientFactory
        services.AddSingleton<RiotService>();
        services.AddSingleton<GeminiService>();
        services.AddSingleton<PolitechnikaService>();
        
        // Register background watcher services
        services.AddSingleton<PolitechnikaWatcherService>(sp => new PolitechnikaWatcherService(
            sp.GetRequiredService<PolitechnikaService>(),
            sp.GetRequiredService<IHttpClientFactory>(),
            settings.DataPath));
        
        return services;
    }

    /// <summary>
    /// Configures the Riot Games API HTTP client with rate limiting and retry policies.
    /// </summary>
    private static void AddRiotHttpClient(this IServiceCollection services, AppSettings settings)
    {
        Log.Information("Token length: {Length}", settings.RiotToken?.Length);
        var cleanToken = settings.RiotToken?.Trim();
        
        services.AddHttpClient(HttpClientNames.RiotApi, client =>
            {
                client.Timeout = TimeSpan.FromSeconds(30);
                client.DefaultRequestHeaders.Add("X-Riot-Token", cleanToken);
            })
            .AddPolicyHandler(GetRetryPolicy("Riot"));
    }

    /// <summary>
    /// Configures the Google Gemini API HTTP client with retry policies.
    /// </summary>
    private static void AddGeminiHttpClient(this IServiceCollection services)
    {
        services.AddHttpClient(HttpClientNames.GeminiApi, client =>
            {
                client.Timeout = TimeSpan.FromSeconds(30);
            })
            .AddPolicyHandler(GetRetryPolicy("Gemini"));
    }

    /// <summary>
    /// Creates a shared Polly retry policy for HTTP requests with exponential backoff.
    /// </summary>
    private static IAsyncPolicy<HttpResponseMessage> GetRetryPolicy(string serviceName)
    {
        return HttpPolicyExtensions
            .HandleTransientHttpError()
            .OrResult(msg => msg.StatusCode == HttpStatusCode.TooManyRequests)
            .WaitAndRetryAsync(
                retryCount: 3,
                sleepDurationProvider: (retryAttempt, response, _) =>
                {
                    if (response.Result?.StatusCode == HttpStatusCode.TooManyRequests &&
                        response.Result.Headers.RetryAfter?.Delta is { } retryAfter)
                    {
                        return retryAfter;
                    }
                    return TimeSpan.FromSeconds(Math.Pow(2, retryAttempt));
                },
                onRetryAsync: (outcome, timespan, retryAttempt, _) =>
                {
                    Log.Warning("{Service} API retry {Attempt} after {Delay}s. Reason: {Reason}",
                        serviceName,
                        retryAttempt,
                        timespan.TotalSeconds,
                        outcome.Result?.StatusCode.ToString() ?? outcome.Exception?.Message);
                    return Task.CompletedTask;
                });
    }
}

/// <summary>
/// Constants for named HTTP client registration.
/// </summary>
public static class HttpClientNames
{
    public const string RiotApi = "RiotApi";
    public const string GeminiApi = "GeminiApi";
}
