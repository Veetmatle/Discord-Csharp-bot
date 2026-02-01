using Discord_Bot_AI.Data;
using Discord;
using Discord.WebSocket;
using Discord_Bot_AI.Models;
using Discord_Bot_AI.Configuration;
using Discord_Bot_AI.Strategy.Notification;
using Discord_Bot_AI.Strategy.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace Discord_Bot_AI.Services;

/// <summary>
/// Main orchestrator service for the Discord bot, managing all subsystems and their lifecycle.
/// Implements IAsyncDisposable for graceful shutdown support (Docker-friendly).
/// </summary>
public class BotService : IAsyncDisposable
{
    private readonly DiscordSocketClient _client;
    private readonly AppSettings _settings;
    private readonly IServiceProvider _serviceProvider;
    private readonly GeminiService _gemini;
    private readonly RiotService _riot;
    private readonly RiotImageCacheService _imageCache;
    private readonly IGameSummaryRenderer _renderer;
    private readonly IUserRegistry _userRegistry;
    private readonly IGuildConfigRegistry _guildConfigRegistry;
    private readonly List<IMatchNotification> _notificationStrategies = new();
    private readonly CancellationTokenSource _shutdownCts = new();
    private DateTime _startTime;
    private bool _disposed;

    /// <summary>
    /// Creates a new BotService with dependencies injected from DI container.
    /// </summary>
    /// <param name="serviceProvider">DI service provider for resolving dependencies.</param>
    public BotService(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
        _settings = serviceProvider.GetRequiredService<AppSettings>();
        _gemini = serviceProvider.GetRequiredService<GeminiService>();
        _riot = serviceProvider.GetRequiredService<RiotService>();
        _imageCache = serviceProvider.GetRequiredService<RiotImageCacheService>();
        _renderer = serviceProvider.GetRequiredService<IGameSummaryRenderer>();
        _userRegistry = serviceProvider.GetRequiredService<IUserRegistry>();
        _guildConfigRegistry = serviceProvider.GetRequiredService<IGuildConfigRegistry>();
        
        var config = new DiscordSocketConfig { GatewayIntents = GatewayIntents.AllUnprivileged | GatewayIntents.MessageContent };
        _client = new DiscordSocketClient(config);
        _client.Ready += OnReadyAsync;
        _client.SlashCommandExecuted += OnSlashCommandAsync;
        _client.Log += OnDiscordLogAsync;
        _client.JoinedGuild += OnJoinedGuildAsync;
        _client.LeftGuild += OnLeftGuildAsync;
    }

    /// <summary>
    /// Starts the bot and all its subsystems. Blocks until shutdown is requested.
    /// </summary>
    public async Task RunAsync()
    {
        _startTime = DateTime.UtcNow;
        
        ValidateSettings();

        InitializeNotificationStrategies();

        RegisterShutdownHandlers();

        await StartDiscordClientAsync();
        
        StartBackgroundMonitoring();
        
        Log.Information("Bot started successfully. Uptime tracking started.");

        try
        {
            await Task.Delay(Timeout.Infinite, _shutdownCts.Token);
        }
        catch (OperationCanceledException)
        {
            Log.Information("Shutdown signal received");
        }
        
        await ShutdownAsync();
    }

    /// <summary>
    /// Validates the application settings.
    /// </summary>
    private void ValidateSettings()
    {
        var errors = new List<string>();
        
        if (string.IsNullOrWhiteSpace(_settings.DiscordToken))
            errors.Add("DISCORD_TOKEN is required");
        if (string.IsNullOrWhiteSpace(_settings.GeminiApiKey))
            errors.Add("GEMINI_API_KEY is required");
        if (string.IsNullOrWhiteSpace(_settings.RiotToken))
            errors.Add("RIOT_TOKEN is required");
            
        if (errors.Count > 0)
        {
            foreach (var error in errors)
                Log.Error("Configuration error: {Error}", error);
            throw new InvalidOperationException($"Configuration validation failed with {errors.Count} error(s)");
        }
        
        Log.Information("Configuration validated successfully");
    }

    /// <summary>
    /// Returns the current health status of the bot for monitoring purposes.
    /// </summary>
    public HealthStatus GetHealthStatus()
    {
        return new HealthStatus
        {
            IsHealthy = _client.ConnectionState == ConnectionState.Connected,
            Uptime = DateTime.UtcNow - _startTime,
            ConnectionState = _client.ConnectionState.ToString(),
            TrackedUsersCount = _userRegistry.GetAllTrackedUsers().Count,
            RiotApiRateLimited = _riot.IsRateLimited,
            GeminiApiRateLimited = _gemini.IsRateLimited
        };
    }

    /// <summary>
    /// Handles Discord.NET log messages and forwards them to Serilog.
    /// </summary>
    private Task OnDiscordLogAsync(LogMessage msg)
    {
        var severity = msg.Severity switch
        {
            LogSeverity.Critical => Serilog.Events.LogEventLevel.Fatal,
            LogSeverity.Error => Serilog.Events.LogEventLevel.Error,
            LogSeverity.Warning => Serilog.Events.LogEventLevel.Warning,
            LogSeverity.Info => Serilog.Events.LogEventLevel.Information,
            LogSeverity.Verbose => Serilog.Events.LogEventLevel.Verbose,
            LogSeverity.Debug => Serilog.Events.LogEventLevel.Debug,
            _ => Serilog.Events.LogEventLevel.Information
        };
        
        Log.Write(severity, msg.Exception, "[Discord] {Message}", msg.Message);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Registers handlers for OS shutdown signals (SIGTERM, SIGINT, etc.).
    /// </summary>
    private void RegisterShutdownHandlers()
    {
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Log.Information("SIGINT received, initiating shutdown...");
            _shutdownCts.Cancel();
        };
        
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            Log.Information("Process exit detected, initiating shutdown...");
            _shutdownCts.Cancel();
        };
    }

    /// <summary>
    /// Gracefully shuts down all bot subsystems.
    /// </summary>
    private async Task ShutdownAsync()
    {
        Log.Information("Shutting down...");
        
        var stopTasks = _notificationStrategies.Select(s => s.StopMonitoringAsync()).ToList();
        await Task.WhenAll(stopTasks);
        
        await _client.StopAsync();
        await _client.LogoutAsync();
        
        Log.Information("Shutdown complete");
    }

    /// <summary>
    /// Initializes notification strategies for match detection.
    /// </summary>
    private void InitializeNotificationStrategies()
    {
        _notificationStrategies.Add(new PollingStrategy(_riot, _userRegistry, NotifyMatchFinishedAsync));
        _notificationStrategies.Add(new CommandStrategy(_riot, _userRegistry, NotifyMatchFinishedAsync));
        Log.Information("Notification strategies initialized: {Count}", _notificationStrategies.Count);
    }

    private async Task StartDiscordClientAsync()
    {
        await _client.LoginAsync(TokenType.Bot, _settings.DiscordToken);
        await _client.StartAsync();
    }

    private void StartBackgroundMonitoring()
    {
        foreach (var strategy in _notificationStrategies)
        {
            _ = strategy.StartMonitoringAsync();
        }
        
        _ = RunPeriodicCacheCleanupAsync(_shutdownCts.Token);
    }

    /// <summary>
    /// Runs periodic cache cleanup every week to prevent disk bloat.
    /// </summary>
    private async Task RunPeriodicCacheCleanupAsync(CancellationToken cancellationToken)
    {
        var cleanupInterval = TimeSpan.FromDays(7);
        var maxFileAge = TimeSpan.FromDays(30);
        
        try
        {
            using var timer = new PeriodicTimer(cleanupInterval);
            
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                Log.Information("Running scheduled cache cleanup...");
                var deleted = _imageCache.CleanupOldFiles(maxFileAge);
                Log.Information("Cache cleanup complete. Deleted {Count} files", deleted);
            }
        }
        catch (OperationCanceledException)
        {
            Log.Debug("Cache cleanup task stopped");
        }
    }

    /// <summary>
    /// Called when the bot joins a new guild. Registers slash commands for that guild.
    /// </summary>
    private async Task OnJoinedGuildAsync(SocketGuild guild)
    {
        Log.Information("Joined guild: {GuildName} ({GuildId})", guild.Name, guild.Id);
        await RegisterCommandsForGuildAsync(guild);
    }

    /// <summary>
    /// Called when the bot leaves a guild. Cleans up guild configuration.
    /// </summary>
    private Task OnLeftGuildAsync(SocketGuild guild)
    {
        Log.Information("Left guild: {GuildName} ({GuildId})", guild.Name, guild.Id);
        _guildConfigRegistry.RemoveGuild(guild.Id);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Called when the Discord client is ready. Registers commands for all connected guilds.
    /// </summary>
    private async Task OnReadyAsync()
    {
        Log.Information("Bot is ready. Connected to {GuildCount} guilds", _client.Guilds.Count);
        
        foreach (var guild in _client.Guilds)
        {
            await RegisterCommandsForGuildAsync(guild);
        }
    }

    /// <summary>
    /// Registers slash commands for a specific guild.
    /// </summary>
    private async Task RegisterCommandsForGuildAsync(SocketGuild guild)
    {
        var command = new SlashCommandBuilder()
            .WithName("laskbot")
            .WithDescription("bot main command")
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("ask")
                .WithDescription("write your question")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption("query", ApplicationCommandOptionType.String, "here provide content", isRequired: true))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("info")
                .WithDescription("show info about bot")
                .WithType(ApplicationCommandOptionType.SubCommand))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("unregister")
                .WithDescription("Use to unregister your League of Legends account")
                .WithType(ApplicationCommandOptionType.SubCommand))
            .AddOption(new SlashCommandOptionBuilder() 
                .WithName("register")
                .WithDescription("Register your League of Legends account")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption("nick", ApplicationCommandOptionType.String, "Your nick in game", isRequired: true)
                .AddOption("tag", ApplicationCommandOptionType.String, "Your tag (eg. EUNE, PL1)", isRequired: true))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("setup-channel")
                .WithDescription("Set the notification channel for match results (Admin only)")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption("channel", ApplicationCommandOptionType.Channel, "The channel for notifications", isRequired: true))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("status")
                .WithDescription("Show bot status and statistics")
                .WithType(ApplicationCommandOptionType.SubCommand))
            .Build();

        try
        {
            await guild.CreateApplicationCommandAsync(command);
            Log.Debug("Registered commands for guild: {GuildName}", guild.Name);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to register commands for guild {GuildId}", guild.Id);
        }
    }

    private async Task OnSlashCommandAsync(SocketSlashCommand command)
    {
        if (command.Data.Name != "laskbot") return;
        
        var subCommand = command.Data.Options.First();
        switch (subCommand.Name)
        {
            case "ask":
                await HandleAskCommandAsync(command, subCommand); 
                break;
            case "info":
                await command.RespondAsync("LaskBot -> v1. Created by Lask.");
                break;
            case "register":
                await RegisterRiotAccountAsync(command, subCommand); 
                break;
            case "unregister":
                await UnregisterRiotAccountAsync(command);
                break;
            case "setup-channel":
                await SetupNotificationChannelAsync(command, subCommand);
                break;
            case "status":
                await ShowStatusAsync(command);
                break;
        }
    }
    
    private async Task UnregisterRiotAccountAsync(SocketSlashCommand command)
    {
        await command.DeferAsync(ephemeral: true);
        
        var guildId = command.GuildId;
        if (guildId == null)
        {
            await command.FollowupAsync("This command must be used in a server.");
            return;
        }
        
        var account = _userRegistry.GetAccount(command.User.Id);
        bool removed = _userRegistry.RemoveUserFromGuild(command.User.Id, guildId.Value);
        
        if (removed)
        {
            if (account != null && account.RegisteredGuildIds.Count > 1)
            {
                await command.FollowupAsync($"Your account **{account.gameName}** has been unregistered from this server. You are still tracked on {account.RegisteredGuildIds.Count - 1} other server(s).");
            }
            else
            {
                await command.FollowupAsync("Your account has been completely unregistered.");
            }
        }
        else
        {
            await command.FollowupAsync("You are not registered on this server.");
        }
    }

    private async Task HandleAskCommandAsync(SocketSlashCommand command, SocketSlashCommandDataOption subCommand)
    {
        await command.DeferAsync();

        var queryOption = subCommand.Options.FirstOrDefault(o => o.Name == "query");
        var question = queryOption?.Value?.ToString() ?? "No question provided";
        var answer = await _gemini.GetAnswerAsync(question, _shutdownCts.Token);

        string response = $"**Question:**\n {question}\n**Answer:**\n {answer}";
        await command.FollowupAsync(response);
    }

    private async Task RegisterRiotAccountAsync(SocketSlashCommand command, SocketSlashCommandDataOption subCommand)
    {
        await command.DeferAsync();
        
        var nick = subCommand.Options.FirstOrDefault(o => o.Name == "nick")?.Value?.ToString();
        var tag = subCommand.Options.FirstOrDefault(o => o.Name == "tag")?.Value?.ToString();

        if (string.IsNullOrEmpty(nick) || string.IsNullOrEmpty(tag))
        {
            await command.FollowupAsync("Invalid nick or tag.");
            return;
        }
        
        var guildId = command.GuildId;
        if (guildId == null)
        {
            await command.FollowupAsync("This command must be used in a server, not in DMs.");
            return;
        }
        
        var existingAccount = _userRegistry.GetAccount(command.User.Id);
        
        if (existingAccount != null)
        {
            if (existingAccount.RegisteredGuildIds.Contains(guildId.Value))
            {
                await command.FollowupAsync($"You are already registered on this server with account **{existingAccount.gameName}#{existingAccount.tagLine}**.");
                return;
            }
            
            _userRegistry.RegisterUser(command.User.Id, existingAccount, guildId.Value);
            await command.FollowupAsync($"Your account **{existingAccount.gameName}#{existingAccount.tagLine}** is now also tracked on this server.");
            return;
        }
        
        var account = await _riot.GetAccountAsync(nick, tag, _shutdownCts.Token);
        if (account != null)
        {
            account.LastMatchId = await _riot.GetLatestMatchIdAsync(account.puuid, _shutdownCts.Token);
            _userRegistry.RegisterUser(command.User.Id, account, guildId.Value);
            await command.FollowupAsync($"Account registered: **{account.gameName}#{account.tagLine}**.");
        }
        else
        {
            await command.FollowupAsync($"Account not found: **{nick}#{tag}**.");
        }
    }
    
    /// <summary>
    /// Sets up the notification channel for a guild. Requires administrator permissions.
    /// </summary>
    private async Task SetupNotificationChannelAsync(SocketSlashCommand command, SocketSlashCommandDataOption subCommand)
    {
        var guildUser = command.User as SocketGuildUser;
        if (guildUser == null || !guildUser.GuildPermissions.Administrator)
        {
            await command.RespondAsync("You need Administrator permissions to use this command.", ephemeral: true);
            return;
        }

        var channelOption = subCommand.Options.FirstOrDefault(o => o.Name == "channel");
        if (channelOption?.Value is not ITextChannel textChannel)
        {
            await command.RespondAsync("Please select a valid text channel.", ephemeral: true);
            return;
        }

        var guildId = command.GuildId;
        if (guildId == null)
        {
            await command.RespondAsync("This command must be used in a server.", ephemeral: true);
            return;
        }

        _guildConfigRegistry.SetNotificationChannel(guildId.Value, textChannel.Id);
        await command.RespondAsync($"Notification channel set to <#{textChannel.Id}>. Match results will be posted there.", ephemeral: true);
        Log.Information("Guild {GuildId} notification channel set to {ChannelId}", guildId.Value, textChannel.Id);
    }

    /// <summary>
    /// Shows bot status including uptime, tracked players, and connected servers.
    /// </summary>
    private async Task ShowStatusAsync(SocketSlashCommand command)
    {
        var uptime = DateTime.UtcNow - _startTime;
        var trackedUsers = _userRegistry.GetAllTrackedUsers().Count;
        var connectedGuilds = _client.Guilds.Count;
        var cacheStats = _imageCache.GetCacheStats();
        
        string uptimeText;
        if (uptime.TotalDays >= 1)
            uptimeText = $"{(int)uptime.TotalDays} day(s), {uptime.Hours} hour(s)";
        else if (uptime.TotalHours >= 1)
            uptimeText = $"{(int)uptime.TotalHours} hour(s), {uptime.Minutes} minute(s)";
        else
            uptimeText = $"{uptime.Minutes} minute(s), {uptime.Seconds} second(s)";
        
        var status = new System.Text.StringBuilder();
        status.AppendLine($"**LaskBot Status**");
        status.AppendLine($"Running for: {uptimeText}");
        status.AppendLine($"Tracking: {trackedUsers} player(s)");
        status.AppendLine($"Connected to: {connectedGuilds} server(s)");
        status.AppendLine($"Cache: {cacheStats.FileCount} files ({cacheStats.TotalSizeMB:F1} MB)");
        
        if (_riot.IsRateLimited)
            status.AppendLine("Riot API: Rate limited");
        if (_gemini.IsRateLimited)
            status.AppendLine("Gemini API: Rate limited");
        
        await command.RespondAsync(status.ToString(), ephemeral: true);
    }
    
    /// <summary>
    /// Sends match notification to all guilds where the account is registered.
    /// Renders the image once and sends it to all configured notification channels.
    /// </summary>
    /// <param name="account">The Riot account of the player.</param>
    /// <param name="matchData">The match data to display.</param>
    /// <param name="ct">Cancellation token to cancel the operation.</param>
    private async Task NotifyMatchFinishedAsync(RiotAccount account, MatchData matchData, CancellationToken ct = default)
    {
        if (account.RegisteredGuildIds.Count == 0)
        {
            Log.Warning("Account {PlayerName} has no registered guilds", account.gameName);
            return;
        }

        byte[]? imageData = null;
        
        try
        {
            using var imageStream = await _renderer.RenderSummaryAsync(account, matchData, ct);
            using var memoryStream = new MemoryStream();
            await imageStream.CopyToAsync(memoryStream, ct);
            imageData = memoryStream.ToArray();
        }
        catch (OperationCanceledException)
        {
            Log.Debug("Match notification cancelled during rendering");
            return;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to render match summary for {PlayerName}", account.gameName);
            return;
        }

        foreach (var guildId in account.RegisteredGuildIds)
        {
            ct.ThrowIfCancellationRequested();
            
            var guild = _client.GetGuild(guildId);
            if (guild == null)
            {
                Log.Warning("Guild {GuildId} not found for account {PlayerName}", guildId, account.gameName);
                continue;
            }

            var channelId = _guildConfigRegistry.GetNotificationChannel(guildId);
            if (!channelId.HasValue)
            {
                Log.Warning("Notification channel not configured for guild {GuildName}. Use /laskbot setup-channel to configure.", guild.Name);
                continue;
            }

            var channel = guild.GetTextChannel(channelId.Value);
            if (channel == null)
            {
                Log.Warning("Channel {ChannelId} not found in guild {GuildName}", channelId.Value, guild.Name);
                await NotifyAdminAboutBrokenConfigAsync(guild);
                _guildConfigRegistry.RemoveGuild(guildId);
                continue;
            }

            try
            {
                using var sendStream = new MemoryStream(imageData);
                await channel.SendFileAsync(sendStream, "match.png", $"**{account.gameName}** finished a match!",
                    options: new RequestOptions { CancelToken = ct });
                Log.Debug("Sent match notification to guild {GuildName}", guild.Name);
            }
            catch (OperationCanceledException)
            {
                Log.Debug("Match notification cancelled during send to guild {GuildId}", guildId);
                throw;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to send match notification to guild {GuildName}", guild.Name);
            }
        }
    }
    
    private async Task NotifyAdminAboutBrokenConfigAsync(SocketGuild guild)
    {
        string errorMessage = $"Attention: channel for league notifications on server **{guild.Name}** has been removed or the bot cannot reach it. \n" +
                              "Notifications have been stopped. Let the admin use commend `/laskbot setup-channel` to set a new notification channel.";

        try
        {
            if (guild.SystemChannel != null)
            {
                await guild.SystemChannel.SendMessageAsync(errorMessage);
                return;
            }
            
            var fallbackChannel = guild.TextChannels
                .FirstOrDefault(c => guild.CurrentUser.GetPermissions(c).SendMessages);
            
            if (fallbackChannel != null)
            {
                await fallbackChannel.SendMessageAsync(errorMessage);
                return;
            }
            
            var owner = guild.Owner;
            if (owner != null)
            {
                await owner.SendMessageAsync(errorMessage);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not send broken config notification to guild {GuildId}", guild.Id);
        }
    }

    /// <summary>
    /// Releases all resources used by the bot service.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        
        _shutdownCts.Cancel();
        _shutdownCts.Dispose();
        
        await _client.DisposeAsync();
        
        // Automatic: ServiceProvider disposes all registered IDisposable singletons
        // (RiotService, GeminiService, UserRegistry, GuildConfigRegistry, RiotImageCacheService, ImageSharpRenderer)
        if (_serviceProvider is IDisposable disposable)
            disposable.Dispose();
        
        _disposed = true;
        Log.Debug("BotService disposed");
        GC.SuppressFinalize(this);
    }
}