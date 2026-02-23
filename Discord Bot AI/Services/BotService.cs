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
    private readonly PolitechnikaService _politechnika;
    private readonly PolitechnikaWatcherService _politechnikaWatcher;
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
        _politechnika = serviceProvider.GetRequiredService<PolitechnikaService>();
        _politechnikaWatcher = serviceProvider.GetRequiredService<PolitechnikaWatcherService>();
        
        var config = new DiscordSocketConfig { GatewayIntents = GatewayIntents.AllUnprivileged | GatewayIntents.MessageContent };
        _client = new DiscordSocketClient(config);
        _client.Ready += OnReadyAsync;
        _client.SlashCommandExecuted += OnSlashCommandAsync;
        _client.MessageReceived += OnMessageReceivedAsync;
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
        
        await MigrateUserPuuidsAsync();
        
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
    /// Migrates user PUUIDs after API key change. Riot encrypts PUUIDs per API key,
    /// so after changing to a new key, all existing PUUIDs must be refreshed.
    /// </summary>
    private async Task MigrateUserPuuidsAsync()
    {
        var users = _userRegistry.GetAllTrackedUsers();
        if (users.Count == 0)
            return;
        
        int migrated = 0;
        int failed = 0;

        foreach (var entry in users)
        {
            var discordUserId = entry.Key;
            var account = entry.Value;

            try
            {
                var refreshedAccount = await _riot.GetAccountAsync(account.gameName, account.tagLine, _shutdownCts.Token);
                
                if (refreshedAccount != null && refreshedAccount.puuid != account.puuid)
                {
                    _userRegistry.UpdateAccountPuuid(discordUserId, refreshedAccount.puuid);
                    migrated++;
                }
                else if (refreshedAccount == null)
                {
                    Log.Warning("Could not refresh account {GameName}#{Tag} - account may no longer exist", 
                        account.gameName, account.tagLine);
                    failed++;
                }
                
                await Task.Delay(1500, _shutdownCts.Token);
            }
            catch (OperationCanceledException)
            {
                Log.Warning("PUUID migration cancelled");
                break;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error migrating PUUID for {GameName}", account.gameName);
                failed++;
            }
        }

        Log.Information("PUUID migration completed: {Migrated} migrated, {Failed} failed", migrated, failed);
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
        stopTasks.Add(_politechnikaWatcher.StopWatchingAsync());
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
        
        _politechnikaWatcher.OnChangeDetected = HandlePolitechnikaChangeAsync;
        _ = _politechnikaWatcher.StartWatchingAsync(_shutdownCts.Token);
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
                .WithDescription("Ask AI a question (for multiple files use: !ask) :D")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption("query", ApplicationCommandOptionType.String, "your question", isRequired: true)
                .AddOption("attachment", ApplicationCommandOptionType.Attachment, "attach an image or document (optional)", isRequired: false))
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
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("check-latest-match")
                .WithDescription("Check and display your latest match result")
                .WithType(ApplicationCommandOptionType.SubCommand))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("pk")
                .WithDescription("Search Politechnika Krakowska WIiT resources (plans, schedules, documents)")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption("query", ApplicationCommandOptionType.String, "What are you looking for? (e.g. 'plan zajęć informatyka I stopień')", isRequired: true))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("pk-watch-start")
                .WithDescription("Start watching for schedule updates on this channel")
                .WithType(ApplicationCommandOptionType.SubCommand))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("pk-watch-stop")
                .WithDescription("Stop watching for schedule updates on this channel")
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

    /// <summary>
    /// Handles incoming slash commands. Offloads processing to a background task to avoid blocking the gateway.
    /// </summary>
    private Task OnSlashCommandAsync(SocketSlashCommand command)
    {
        if (command.Data.Name != "laskbot") return Task.CompletedTask;
        
        _ = Task.Run(async () =>
        {
            try
            {
                var subCommand = command.Data.Options.First();
                switch (subCommand.Name)
                {
                    case "ask":
                        await HandleAskCommandAsync(command, subCommand); 
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
                    case "check-latest-match":
                        await CheckLatestMatchAsync(command);
                        break;
                    case "pk":
                        await HandlePolitechnikaQueryAsync(command, subCommand);
                        break;
                    case "pk-watch-start":
                        await HandlePkWatchStartAsync(command);
                        break;
                    case "pk-watch-stop":
                        await HandlePkWatchStopAsync(command);
                        break;
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error handling slash command {CommandName}", command.Data.Name);
                try
                {
                    if (!command.HasResponded)
                    {
                        await command.RespondAsync("An error occurred while processing your command.", ephemeral: true);
                    }
                }
                catch
                {
                }
            }
        });
        
        return Task.CompletedTask;
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

    /// <summary>
    /// Handles the ask command, supporting text prompts with optional image/document attachments.
    /// For multiple attachments, use text command: !ask [question] with attached files.
    /// </summary>
    private async Task HandleAskCommandAsync(SocketSlashCommand command, SocketSlashCommandDataOption subCommand)
    {
        await command.DeferAsync();

        var queryOption = subCommand.Options.FirstOrDefault(o => o.Name == "query");
        var attachmentOption = subCommand.Options.FirstOrDefault(o => o.Name == "attachment");
        
        var question = queryOption?.Value?.ToString() ?? "No question provided";
        
        var request = new GeminiRequest { Prompt = question };
        
        if (attachmentOption?.Value is IAttachment attachment)
        {
            AddAttachmentToRequest(request, attachment);
        }
        
        var answer = await _gemini.GetAnswerAsync(request, _shutdownCts.Token);

        string response = BuildAskResponse(question, request.Attachments, answer);
        await command.FollowupAsync(response);
    }
    
    /// <summary>
    /// Handles Politechnika Krakowska WIiT query - searches for documents, schedules, plans etc.
    /// </summary>
    private async Task HandlePolitechnikaQueryAsync(SocketSlashCommand command, SocketSlashCommandDataOption subCommand)
    {
        await command.DeferAsync();
        
        var queryOption = subCommand.Options.FirstOrDefault(o => o.Name == "query");
        var userQuery = queryOption?.Value?.ToString() ?? "";
        
        if (string.IsNullOrWhiteSpace(userQuery))
        {
            await command.FollowupAsync("Podaj zapytanie, np. 'plan zajęć informatyka I stopień'.");
            return;
        }
        
        Log.Information("Processing Politechnika query: {Query} from user {User}", userQuery, command.User.Username);
        
        var result = await _politechnika.ProcessQueryAsync(userQuery, _shutdownCts.Token);
        
        var sb = new System.Text.StringBuilder();
        
        if (result.Success && !string.IsNullOrEmpty(result.Url))
        {
            // Short answer from Gemini first (if available)
            if (!string.IsNullOrWhiteSpace(result.Answer))
            {
                sb.AppendLine($"📚 {result.Answer}");
                sb.AppendLine();
            }
            
            if (result.IsFile)
            {
                var downloadInfo = await _politechnika.CheckFileDownloadableAsync(result.Url, _shutdownCts.Token);
                
                if (downloadInfo.CanDownload)
                {
                    var downloadResult = await _politechnika.DownloadFileAsync(result.Url, _shutdownCts.Token);
                    
                    if (downloadResult.HasValue && downloadResult.Value.Stream != null)
                    {
                        sb.AppendLine($"**Plik ({result.FileType}):** {result.LinkText}");
                        sb.AppendLine($"*Źródło: {result.SourceUrl}*");
                        
                        await using var stream = downloadResult.Value.Stream;
                        var attachment = new FileAttachment(stream, downloadResult.Value.FileName);
                        await command.FollowupWithFileAsync(attachment, text: sb.ToString());
                        return;
                    }
                }
                sb.AppendLine($"**Plik ({result.FileType}):** {result.LinkText}");
                if (!downloadInfo.CanDownload && downloadInfo.Reason != null)
                {
                    sb.AppendLine($"*({downloadInfo.Reason})*");
                }
                sb.AppendLine($"**Link:** {result.Url}");
            }
            else
            {
                sb.AppendLine($"**Link:** {result.Url}");
            }
            
            sb.AppendLine();
            sb.AppendLine($"*Źródło: {result.SourceUrl}*");
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(result.Answer))
            {
                sb.AppendLine($"📚 {result.Answer}");
                sb.AppendLine();
            }
            sb.AppendLine($"**Wynik:** {result.Message}");
            sb.AppendLine();
            sb.AppendLine($"**Sprawdź stronę bezpośrednio:** {result.SourceUrl}");
        }
        
        await command.FollowupAsync(sb.ToString());
    }
    
    /// <summary>
    /// Handles text-based messages starting with !ask prefix.
    /// Supports multiple attachments (images, documents, screenshots).
    /// Usage: !ask [your question] + attach files to the message.
    /// </summary>
    private Task OnMessageReceivedAsync(SocketMessage message)
    {
        if (message.Author.IsBot) return Task.CompletedTask;
        if (message is not SocketUserMessage) return Task.CompletedTask;
        
        var content = message.Content.Trim();
        if (!content.StartsWith("!ask", StringComparison.OrdinalIgnoreCase)) return Task.CompletedTask;
        
        _ = Task.Run(async () =>
        {
            try
            {
                var question = content.Length > 4 
                    ? content[4..].Trim() 
                    : "Please analyze the attached content.";
                
                if (string.IsNullOrWhiteSpace(question) && message.Attachments.Count == 0)
                {
                    await message.Channel.SendMessageAsync("Please provide a question or attach files to analyze.");
                    return;
                }
                
                using var typingState = message.Channel.EnterTypingState();
                
                var request = new GeminiRequest { Prompt = question };
                
                foreach (var attachment in message.Attachments)
                {
                    AddAttachmentToRequest(request, attachment);
                }
                
                if (request.Attachments.Count > 0)
                {
                    Log.Information("Processing !ask command with {Count} attachment(s) from user {User}", 
                        request.Attachments.Count, message.Author.Username);
                }
                
                var answer = await _gemini.GetAnswerAsync(request, _shutdownCts.Token);
                
                string response = BuildAskResponse(question, request.Attachments, answer);
                await message.Channel.SendMessageAsync(response, messageReference: new MessageReference(message.Id));
            }
            catch (OperationCanceledException)
            {
                Log.Debug("!ask command cancelled");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error processing !ask command");
                await message.Channel.SendMessageAsync("An error occurred while processing your request.");
            }
        });
        
        return Task.CompletedTask;
    }
    
    /// <summary>
    /// Adds a Discord attachment to a Gemini request, validating size and type.
    /// </summary>
    private static void AddAttachmentToRequest(GeminiRequest request, IAttachment attachment)
    {
        var geminiAttachment = new GeminiAttachment
        {
            Url = attachment.Url,
            MimeType = attachment.ContentType ?? "application/octet-stream",
            FileName = attachment.Filename,
            Size = attachment.Size
        };
        
        if (!GeminiSupportedTypes.IsSupported(geminiAttachment.MimeType))
        {
            Log.Warning("Skipping unsupported attachment type: {FileName} ({MimeType})", 
                attachment.Filename, attachment.ContentType);
            return;
        }
        
        if (geminiAttachment.Size > GeminiSupportedTypes.MaxFileSizeBytes)
        {
            Log.Warning("Skipping attachment too large: {FileName} ({Size} bytes)", 
                attachment.Filename, geminiAttachment.Size);
            return;
        }
        
        request.Attachments.Add(geminiAttachment);
        Log.Debug("Attachment added to Gemini request: {FileName} ({MimeType})", 
            attachment.Filename, attachment.ContentType);
    }
    
    /// <summary>
    /// Builds the response message for ask command, including attachment info.
    /// </summary>
    private static string BuildAskResponse(string question, List<GeminiAttachment> attachments, string answer)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"**Question:** {question}");
        
        if (attachments.Count > 0)
        {
            var fileNames = string.Join(", ", attachments.Select(a => a.FileName));
            sb.AppendLine($"**Attachments ({attachments.Count}):** {fileNames}");
        }
        
        sb.AppendLine($"**Answer:** {answer}");
        return sb.ToString();
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
    /// Checks and displays the latest match for the requesting user.
    /// Sends the result directly to the channel where the command was invoked.
    /// </summary>
    private async Task CheckLatestMatchAsync(SocketSlashCommand command)
    {
        await command.DeferAsync();
        
        var guildId = command.GuildId;
        if (guildId == null)
        {
            await command.FollowupAsync("This command must be used in a server.");
            return;
        }
        
        var account = _userRegistry.GetAccount(command.User.Id);
        if (account == null)
        {
            await command.FollowupAsync("You are not registered. Use `/laskbot register` first.");
            return;
        }
        
        if (!account.RegisteredGuildIds.Contains(guildId.Value))
        {
            await command.FollowupAsync("You are not registered on this server. Use `/laskbot register` to register here.");
            return;
        }
        
        try
        {
            // Get latest match directly without using the polling strategy's callback
            string? matchId = await _riot.GetLatestMatchIdAsync(account.puuid, _shutdownCts.Token);
            
            if (string.IsNullOrEmpty(matchId))
            {
                await command.FollowupAsync("No recent matches found for your account.");
                return;
            }
            
            var matchData = await _riot.GetMatchDetailsAsync(matchId, _shutdownCts.Token);
            if (matchData == null)
            {
                await command.FollowupAsync("Could not retrieve match details. Please try again later.");
                return;
            }
            
            // Render the match summary
            using var imageStream = await _renderer.RenderSummaryAsync(account, matchData, _shutdownCts.Token);
            using var memoryStream = new MemoryStream();
            await imageStream.CopyToAsync(memoryStream, _shutdownCts.Token);
            memoryStream.Position = 0;
            
            var me = matchData.info.participants.FirstOrDefault(p => p.puuid == account.puuid);
            string resultText = me?.win == true ? "Victory" : "Defeat";
            
            // Send directly to the channel where command was invoked
            await command.FollowupWithFileAsync(
                memoryStream,
                "match_summary.png",
                $"**{account.gameName}#{account.tagLine}** - {resultText}"
            );
        }
        catch (OperationCanceledException)
        {
            await command.FollowupAsync("Operation was cancelled.");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to check latest match for user {UserId}", command.User.Id);
            await command.FollowupAsync("Failed to retrieve your latest match. Please try again later.");
        }
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
                await channel.SendFileAsync(sendStream, "match.png", $"**{account.gameName}** finished a match :)",
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
    /// Starts watching for Politechnika schedule updates on current channel.
    /// </summary>
    private async Task HandlePkWatchStartAsync(SocketSlashCommand command)
    {
        var guildId = command.GuildId;
        var channelId = command.ChannelId;
        
        if (!guildId.HasValue || !channelId.HasValue)
        {
            await command.RespondAsync("Ta komenda działa tylko na serwerze.", ephemeral: true);
            return;
        }
        
        if (await _politechnikaWatcher.IsChannelSubscribedAsync(guildId.Value, channelId.Value, _shutdownCts.Token))
        {
            await command.RespondAsync("📚 Ten kanał już obserwuje aktualizacje planów zajęć.", ephemeral: true);
            return;
        }
        
        var success = await _politechnikaWatcher.SubscribeChannelAsync(
            guildId.Value, channelId.Value, command.User.Id, _shutdownCts.Token);
        
        if (success)
        {
            await command.RespondAsync(
                "✅ **Włączono obserwowanie aktualizacji!**\n\n" +
                "Ten kanał będzie otrzymywać powiadomienia gdy pojawi się nowy plan zajęć lub harmonogram na stronie WIiT PK.\n" +
                "Sprawdzanie odbywa się co 30 minut.\n\n" +
                "Aby wyłączyć: `/laskbot pk-watch-stop`");
        }
        else
        {
            await command.RespondAsync("❌ Nie udało się włączyć obserwowania.", ephemeral: true);
        }
    }
    
    /// <summary>
    /// Stops watching for Politechnika schedule updates on current channel.
    /// </summary>
    private async Task HandlePkWatchStopAsync(SocketSlashCommand command)
    {
        var guildId = command.GuildId;
        var channelId = command.ChannelId;
        
        if (!guildId.HasValue || !channelId.HasValue)
        {
            await command.RespondAsync("Ta komenda działa tylko na serwerze.", ephemeral: true);
            return;
        }
        
        if (!await _politechnikaWatcher.IsChannelSubscribedAsync(guildId.Value, channelId.Value, _shutdownCts.Token))
        {
            await command.RespondAsync("📚 Ten kanał nie obserwuje aktualizacji planów zajęć.", ephemeral: true);
            return;
        }
        
        var success = await _politechnikaWatcher.UnsubscribeChannelAsync(
            guildId.Value, channelId.Value, _shutdownCts.Token);
        
        if (success)
        {
            await command.RespondAsync("✅ **Wyłączono obserwowanie aktualizacji.**\n\nTen kanał nie będzie już otrzymywać powiadomień o planach zajęć.");
        }
        else
        {
            await command.RespondAsync("❌ Nie udało się wyłączyć obserwowania.", ephemeral: true);
        }
    }
    
    /// <summary>
    /// Handles Politechnika website change notifications from the watcher service.
    /// Sends notifications only to subscribed channels.
    /// </summary>
    private async Task HandlePolitechnikaChangeAsync(PolitechnikaChangeNotification notification, CancellationToken ct)
    {
        if (notification.SubscribedChannels.Count == 0)
        {
            Log.Debug("No subscribed channels for Politechnika notifications");
            return;
        }
        
        // Build notification message
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("📚 **Politechnika Krakowska - Aktualizacja!**");
        sb.AppendLine();
        
        if (notification.ChangeType == ChangeType.New)
        {
            sb.AppendLine($"🆕 **Nowy dokument:** {notification.PatternName}");
        }
        else
        {
            sb.AppendLine($"🔄 **Aktualizacja:** {notification.PatternName}");
            if (notification.OldDate != null && notification.NewDate != null)
            {
                sb.AppendLine($"📅 Data zmieniła się z `{notification.OldDate}` na `{notification.NewDate}`");
            }
        }
        
        sb.AppendLine();
        sb.AppendLine($"📄 **{notification.LinkText}**");
        sb.AppendLine($"🔗 {notification.LinkUrl}");
        
        var message = sb.ToString();
        
        // Send to each subscribed channel
        foreach (var subscription in notification.SubscribedChannels)
        {
            ct.ThrowIfCancellationRequested();
            
            var guild = _client.GetGuild(subscription.GuildId);
            if (guild == null)
            {
                Log.Warning("Guild {GuildId} not found for PK notification", subscription.GuildId);
                continue;
            }
            
            var channel = guild.GetTextChannel(subscription.ChannelId);
            if (channel == null)
            {
                Log.Warning("Channel {ChannelId} not found in guild {GuildName}", subscription.ChannelId, guild.Name);
                // Remove invalid subscription
                await _politechnikaWatcher.UnsubscribeChannelAsync(subscription.GuildId, subscription.ChannelId, ct);
                continue;
            }
            
            try
            {
                await channel.SendMessageAsync(message, options: new RequestOptions { CancelToken = ct });
                Log.Information("Sent PK update to channel {ChannelId} in guild {GuildName}", 
                    subscription.ChannelId, guild.Name);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to send PK notification to channel {ChannelId}", subscription.ChannelId);
            }
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
        if (_serviceProvider is IDisposable disposable)
            disposable.Dispose();
        
        _disposed = true;
        Log.Debug("BotService disposed");
        GC.SuppressFinalize(this);
    }
}