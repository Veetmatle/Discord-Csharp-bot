using Discord_Bot_AI.Data;
using Discord;
using Discord.WebSocket;
using Discord_Bot_AI.Models;
using Newtonsoft.Json;

namespace Discord_Bot_AI.Services;

public class BotService
{
    private readonly DiscordSocketClient _client;
    private Config? _config;
    private GeminiService? _gemini;
    private RiotService? _riot;
    private readonly List<ulong> _guildIds = new();
    private readonly UserRegistry _userRegistry = new();
    
    private readonly string _promptPrefix =
        "\n Answer in Polish in max 100 words. Be brief and precise unless instructions say otherwise.";

    public BotService()
    {
        var config = new DiscordSocketConfig { GatewayIntents = GatewayIntents.AllUnprivileged | GatewayIntents.MessageContent };
        _client = new DiscordSocketClient(config);
        _client.Ready += OnReadyAsync;
        _client.SlashCommandExecuted += OnSlashCommandAsync;
    }

    public async Task RunAsync()
    {
        await LoadConfigAsync();
        if (_config == null) return;

        _gemini = new GeminiService(_config.GeminiApiKey);
        _riot = new RiotService(_config.RiotToken);
        
        foreach (var id in _config.ServerIds)
        {
            if (ulong.TryParse(id, out ulong guildId))
            {
                _guildIds.Add(guildId);
            }
        }

        await _client.LoginAsync(TokenType.Bot, _config.DiscordToken);
        await _client.StartAsync();
        _ = StartMatchMonitoringAsync();
        
        await Task.Delay(-1);
    }

    private async Task OnReadyAsync()
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
            .Build();

        foreach (var id in _guildIds)
        {
            var guild = _client.GetGuild(id);
            if (guild != null)
            {
                try
                {
                    await guild.CreateApplicationCommandAsync(command);
                    Console.WriteLine($"Registered for {guild.Name}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error on {id}: {ex.Message}");
                }
            }
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
        }
    }
    
    private async Task UnregisterRiotAccountAsync(SocketSlashCommand command)
    {
        await command.DeferAsync(ephemeral: true);
        bool removed = _userRegistry.RemoveUser(command.User.Id);
        if (removed)
        {
            await command.FollowupAsync("Your account has been unregistered successfully.");
        }
        else
        {
            await command.FollowupAsync("No corresponding account found to unregister.");
        }
    }

    private async Task HandleAskCommandAsync(SocketSlashCommand command, SocketSlashCommandDataOption subCommand)
    {
        await command.DeferAsync();

        var queryOption = subCommand.Options.FirstOrDefault(o => o.Name == "query");
        var question = queryOption?.Value?.ToString() ?? "No question provided";
        var answer = await _gemini!.GetAnswerAsync(question + this._promptPrefix);

        string response = $"**Question:**\n {question}\n**Answer:**\n {answer}";
        await command.FollowupAsync(response);
    }

    private async Task RegisterRiotAccountAsync(SocketSlashCommand command, SocketSlashCommandDataOption subCommand)
    {
        if (_riot == null) return;
        
        await command.DeferAsync();
        
        var nick = subCommand.Options.FirstOrDefault(o => o.Name == "nick")?.Value?.ToString();
        var tag = subCommand.Options.FirstOrDefault(o => o.Name == "tag")?.Value?.ToString();

        if (string.IsNullOrEmpty(nick) || string.IsNullOrEmpty(tag))
        {
            await command.FollowupAsync("Invalid nick or tag.");
            return;
        }
        
        var account = await _riot.GetAccountAsync(nick, tag);
        if (account != null)
        {
            _userRegistry.RegisterUser(command.User.Id, account);
            await command.FollowupAsync($"Account registered: **{account.gameName}#{account.tagLine}**.");
        }
        else
        {
            await command.FollowupAsync($"Account not found: **{nick}#{tag}**.");
        }
    }
    
    private async Task StartMatchMonitoringAsync()
    {
        using PeriodicTimer timer = new PeriodicTimer(TimeSpan.FromMinutes(10));
        while (await timer.WaitForNextTickAsync())
        {
            try
            {
                await CheckForNewMatchesAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Monitoring error: {ex.Message}");
            }
        }
    }

    private async Task CheckForNewMatchesAsync()
    {
        var users = _userRegistry.GetAllTrackedUsers();
        if (!users.Any()) return;

        foreach (var entry in users)
        {
            var account = entry.Value;
            string? currentMatchId = await _riot!.GetLatestMatchIdAsync(account.puuid);
            
            if (!string.IsNullOrEmpty(currentMatchId) && currentMatchId != account.LastMatchId)
            {
                var matchData = await _riot.GetMatchDetailsAsync(currentMatchId);
                if (matchData != null)
                {
                    account.LastMatchId = currentMatchId;
                    _userRegistry.RegisterUser(entry.Key, account); 
                    await NotifyMatchFinishedAsync(account);
                }
            }
        }
    }
    
    private async Task NotifyMatchFinishedAsync(RiotAccount account)
    {
        var guild = _client.GetGuild(_guildIds.FirstOrDefault()); 
        var channel = guild?.TextChannels.FirstOrDefault(c => c.Name == "bot"); 

        if (channel != null)
        {
            await channel.SendMessageAsync($"**{account.gameName}** finished a match.");
        }
    }

    private async Task LoadConfigAsync()
    {
        if (!File.Exists("config.json")) throw new FileNotFoundException();
        string json = await File.ReadAllTextAsync("config.json");
        _config = JsonConvert.DeserializeObject<Config>(json) ?? throw new Exception("Config error");
    }
}