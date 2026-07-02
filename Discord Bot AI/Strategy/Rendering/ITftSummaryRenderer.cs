namespace Discord_Bot_AI.Strategy.Rendering;

using Models;

/// <summary>
/// Interface for rendering TFT match summary images.
/// </summary>
public interface ITftSummaryRenderer
{
    Task<Stream> RenderTftSummaryAsync(RiotAccount account, TftMatchData matchData, CancellationToken cancellationToken = default);
}
