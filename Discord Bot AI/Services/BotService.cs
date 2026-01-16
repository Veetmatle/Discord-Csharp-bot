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
        
        if (_config.ServerIds.Any())
        {
            foreach (var id in _config.ServerIds)
            {
                if (ulong.TryParse(id, out ulong guildId))
                {
                    _guildIds.Add(guildId);
                }
            }
        }

        await _client.LoginAsync(TokenType.Bot, _config.DiscordToken);
        await _client.StartAsync();
        await Task.Delay(-1);
    }

    private async Task LoadConfigAsync()
    {
        if (!File.Exists("config.json"))
            throw new FileNotFoundException();

        string json = await File.ReadAllTextAsync("config.json");
        _config = JsonConvert.DeserializeObject<Config>(json)
                  ?? throw new Exception("Deserialization error.");
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
                .WithName("register lol account")
                .WithDescription("Register your League of Legends account. Provide in-game nick and tag.")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption("nick", ApplicationCommandOptionType.String, "Your nick in game.", isRequired: true)
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
        }
    }

    private async Task HandleAskCommandAsync(SocketSlashCommand command, SocketSlashCommandDataOption subCommand)
    {
        await command.DeferAsync();

        var question = subCommand.Options.First().Value.ToString() ?? "Zwróć odpowiedź: Brak pytania";
        var answer = await _gemini!.GetAnswerAsync(question + this._promptPrefix);

        Console.WriteLine(question);
        Console.WriteLine(answer);

        string response = $"**Question:**\n {question}\n**Answer:**\n {answer}";
        await command.FollowupAsync(response);
    }

    private async Task RegisterRiotAccountAsync(SocketSlashCommand command, SocketSlashCommandDataOption subCommand)
    {
        if (_riot == null)
            return;
        
        await command.DeferAsync();
        
        var nick = subCommand.Options.First(o => o.Name == "nick").Value.ToString();
        var tag = subCommand.Options.First(o => o.Name == "tag").Value.ToString();
        if (nick == null || tag == null)
        {
            await command.FollowupAsync("Invalid nick or tag.");
            return;
        }
        
        var account = await _riot!.GetAccountAsync(nick, tag);
        if (account != null)
        {
            _userRegistry.RegisterUser(command.User.Id, account);
            await command.FollowupAsync($"Operation went well: **{account.gameName}#{account.tagLine}**.");
        }
        else
        {
            await command.FollowupAsync($"Account not found: **{nick}#{tag}**. Validate input data.");
        }
    }
}

