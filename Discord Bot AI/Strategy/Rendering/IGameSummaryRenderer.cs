namespace Discord_Bot_AI.Strategy.Rendering;

using Models;

/// <summary>
/// Interface for rendering visual match summaries.
/// </summary>
public interface IGameSummaryRenderer
{
    /// <summary>
    /// Renders a visual summary of a completed match and returns it as a stream.
    /// </summary>
    /// <param name="account">The Riot account of the player.</param>
    /// <param name="matchData">The match data to render.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>A stream containing the rendered image.</returns>
    Task<Stream> RenderSummaryAsync(RiotAccount account, MatchData matchData, CancellationToken cancellationToken = default);
}