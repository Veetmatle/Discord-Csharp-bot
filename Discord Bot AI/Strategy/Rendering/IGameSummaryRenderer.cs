namespace Discord_Bot_AI.Strategy.Rendering;
using Discord_Bot_AI.Models;

public interface IGameSummaryRenderer
{
    Task<Stream> RenderSummaryAsync(RiotAccount account, MatchData matchData);
}