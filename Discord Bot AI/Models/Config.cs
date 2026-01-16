namespace Discord_Bot_AI.Models;

public class Config
{
    public required string DiscordToken { get; set; }
    public required string GeminiApiKey { get; set; }
    public required string RiotToken { get; set; }
    public required List<string> ServerIds { get; set; }
}

