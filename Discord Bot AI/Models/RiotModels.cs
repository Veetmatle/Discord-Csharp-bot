namespace Discord_Bot_AI.Models;

public class RiotAccount
{
    public string puuid { get; set; }
    public string gameName { get; set; }
    public string tagLine { get; set; }
}

public class MatchData
{
    public Metadata metadata { get; set; }
    public Info info { get; set; }
}

public class Metadata
{
    public string matchId { get; set; }
    public List<string> participants { get; set; }
}

public class Info
{
    public long gameDuration { get; set; } 
    public string gameMode { get; set; }
    public List<Participant> participants { get; set; }
}

public class Participant
{
    public string puuid { get; set; }
    public string summonerName { get; set; }
    public int kills { get; set; }
    public int deaths { get; set; }
    public int assists { get; set; }
    public string championName { get; set; }
    public bool win { get; set; }
}