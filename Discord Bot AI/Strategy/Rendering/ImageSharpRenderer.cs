using Discord_Bot_AI.Models;
using Discord_Bot_AI.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.Fonts;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Drawing.Processing;
using Serilog;

namespace Discord_Bot_AI.Strategy.Rendering;

/// <summary>
/// Renders match summary images using ImageSharp library with concurrency control and timeout protection.
/// Layout mirrors the League of Legends post-game scoreboard.
/// </summary>
public class ImageSharpRenderer : IGameSummaryRenderer, IDisposable
{
    private readonly FontCollection _fontCollection = new();
    private readonly FontFamily _headingFamily;
    private readonly FontFamily _statsFamily;
    private readonly RiotImageCacheService _imageCache;
    
    private readonly SemaphoreSlim _renderQueue = new(2, 2);
    private static readonly TimeSpan RenderTimeout = TimeSpan.FromSeconds(30);
    private bool _disposed;

    // ── Layout constants ────────────────────────────────────────────────
    private const int ImageWidth = 750;
    private const int RowHeight = 44;
    private const int TeamHeaderHeight = 32;
    private const int ColumnHeaderHeight = 22;
    private const int HeaderHeight = 80;
    private const int TeamSpacing = 12;
    private const int BottomPadding = 16;

    // Column X positions
    private const int ColChampIcon = 8;
    private const int ColLevel = 42;        // level badge overlapping champion icon
    private const int ColName = 56;
    private const int ColItems = 170;
    private const int ColKda = 420;
    private const int ColCs = 510;
    private const int ColGold = 570;
    private const int ColDamage = 655;

    // Icon sizes
    private const int ChampIconSize = 32;
    private const int ItemIconSize = 24;
    private const int ItemSpacing = 2;
    private const int TrinketGap = 5;

    /// <summary>
    /// Initializes the renderer, loads font collections from assets, and injects the image cache service.
    /// </summary>
    public ImageSharpRenderer(RiotImageCacheService imageCache)
    {
        _imageCache = imageCache;
        _headingFamily = _fontCollection.Add("Assets/Fonts/Cinzel static/Cinzel-Bold.ttf");
        _statsFamily = _fontCollection.Add("Assets/Fonts/Roboto static/RobotoCondensed-Bold.ttf");
    }

    /// <summary>
    /// Main method to process match data. Prepares graphical assets asynchronously 
    /// and generates the final summary image as a stream with timeout protection.
    /// </summary>
    public async Task<Stream> RenderSummaryAsync(RiotAccount account, MatchData matchData, CancellationToken cancellationToken = default)
    {
        using var timeoutCts = new CancellationTokenSource(RenderTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        
        if (!await _renderQueue.WaitAsync(RenderTimeout, linkedCts.Token))
        {
            Log.Warning("Render queue full, request timed out waiting for slot");
            throw new TimeoutException("Render queue is full. Please try again later.");
        }
        
        try
        {
            return await RenderSummaryInternalAsync(account, matchData, linkedCts.Token);
        }
        finally
        {
            _renderQueue.Release();
        }
    }

    /// <summary>
    /// Internal rendering logic. Renders full match scoreboard with all 10 players split into two teams.
    /// </summary>
    private async Task<Stream> RenderSummaryInternalAsync(RiotAccount account, MatchData matchData, CancellationToken cancellationToken)
    {
        var me = matchData.info.participants.FirstOrDefault(p => p.puuid == account.puuid);
        if (me == null) throw new Exception("Player not found in match data");

        var allParticipants = matchData.info.participants;
        var winningTeam = allParticipants.Where(p => p.win).OrderByDescending(p => p.kills).ToList();
        var losingTeam = allParticipants.Where(p => !p.win).OrderByDescending(p => p.kills).ToList();

        cancellationToken.ThrowIfCancellationRequested();

        var playerAssets = await LoadAllPlayerAssetsAsync(allParticipants, cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();

        // Calculate image height dynamically
        int team1Height = TeamHeaderHeight + ColumnHeaderHeight + (winningTeam.Count * RowHeight);
        int team2Height = TeamHeaderHeight + ColumnHeaderHeight + (losingTeam.Count * RowHeight);
        int imageHeight = HeaderHeight + team1Height + TeamSpacing + team2Height + BottomPadding;

        using Image<Rgba32> image = new(ImageWidth, imageHeight);

        image.Mutate(ctx =>
        {
            DrawBackground(ctx);
            DrawHeader(ctx, me, matchData);
            
            float yOffset = HeaderHeight;
            
            // ── Winning team ────────────────────────────────────────
            DrawTeamHeader(ctx, "VICTORY", Color.FromRgb(70, 130, 180), yOffset);
            yOffset += TeamHeaderHeight;
            DrawTableHeaders(ctx, yOffset);
            yOffset += ColumnHeaderHeight;
            
            foreach (var player in winningTeam)
            {
                var assets = playerAssets[player.puuid];
                bool isTrackedPlayer = player.puuid == account.puuid;
                DrawPlayerRow(ctx, player, yOffset, assets, isTrackedPlayer, isWinningTeam: true);
                yOffset += RowHeight;
            }
            
            yOffset += TeamSpacing;
            
            // ── Losing team ─────────────────────────────────────────
            DrawTeamHeader(ctx, "DEFEAT", Color.FromRgb(180, 70, 70), yOffset);
            yOffset += TeamHeaderHeight;
            DrawTableHeaders(ctx, yOffset);
            yOffset += ColumnHeaderHeight;
            
            foreach (var player in losingTeam)
            {
                var assets = playerAssets[player.puuid];
                bool isTrackedPlayer = player.puuid == account.puuid;
                DrawPlayerRow(ctx, player, yOffset, assets, isTrackedPlayer, isWinningTeam: false);
                yOffset += RowHeight;
            }
        });

        var ms = new MemoryStream();
        await image.SaveAsPngAsync(ms, cancellationToken);
        ms.Position = 0;
        
        Log.Debug("Rendered full match summary for {PlayerName} with {PlayerCount} players", account.gameName, allParticipants.Count);
        return ms;
    }

    /// <summary>
    /// Loads champion and item icons for all participants in parallel.
    /// Items are collected in order: item0–item5 (main slots), item7 (extra/boots slot), item6 (trinket).
    /// Non-empty main items are packed left-to-right; trinket is always last.
    /// </summary>
    private async Task<Dictionary<string, PlayerAssets>> LoadAllPlayerAssetsAsync(List<Participant> participants, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, PlayerAssets>();
        
        var tasks = participants.Select(async p =>
        {
            string championPath = await _imageCache.GetChampionIconAsync(p.championName, cancellationToken);
            
            // Main item slots: item0-item5 + item7 (extra slot for roles like ADC boots)
            // item6 is always the trinket (ward) and rendered separately at the end
            int[] mainItemIds = { p.item0, p.item1, p.item2, p.item3, p.item4, p.item5, p.item7 };
            int trinketId = p.item6;
            
            // Pack non-empty items to the left (filter out 0 = empty slot)
            var nonEmptyMainIds = mainItemIds.Where(id => id != 0).ToList();
            
            // Load icons for non-empty main items
            var mainItemTasks = nonEmptyMainIds.Select(id => _imageCache.GetItemIconAsync(id, cancellationToken));
            var mainItemPaths = (await Task.WhenAll(mainItemTasks)).ToList();
            
            // Load trinket icon (may be empty)
            string trinketPath = trinketId != 0 
                ? await _imageCache.GetItemIconAsync(trinketId, cancellationToken) 
                : "";
            
            return new PlayerAssets
            {
                Puuid = p.puuid,
                ChampionPath = championPath,
                MainItemPaths = mainItemPaths,      // only non-empty, packed left
                TrinketPath = trinketPath,
                MainSlotCount = mainItemIds.Length    // total available slots (for empty slot rendering)
            };
        });

        var allAssets = await Task.WhenAll(tasks);
        foreach (var asset in allAssets)
        {
            result[asset.Puuid] = asset;
        }
        
        return result;
    }

    /// <summary>
    /// Stores loaded image paths for a player, separating main items from trinket.
    /// </summary>
    private class PlayerAssets
    {
        public string Puuid { get; set; } = "";
        public string ChampionPath { get; set; } = "";
        public List<string> MainItemPaths { get; set; } = new();  // packed non-empty items
        public string TrinketPath { get; set; } = "";              // trinket (item6)
        public int MainSlotCount { get; set; } = 7;                // total main slots available
    }

    // ════════════════════════════════════════════════════════════════════
    //  Drawing helpers
    // ════════════════════════════════════════════════════════════════════

    private void DrawBackground(IImageProcessingContext ctx)
    {
        ctx.Fill(Color.FromRgb(10, 20, 25));
    }

    private void DrawHeader(IImageProcessingContext ctx, Participant me, MatchData matchData)
    {
        var titleFont = _headingFamily.CreateFont(28);
        var subFont = _statsFamily.CreateFont(13);

        string title = me.win ? "VICTORY" : "DEFEAT";
        Color titleColor = me.win ? Color.FromRgb(70, 130, 180) : Color.FromRgb(180, 70, 70);

        ctx.DrawText(title, titleFont, titleColor, new PointF(16, 12));

        int minutes = (int)(matchData.info.gameDuration / 60);
        int seconds = (int)(matchData.info.gameDuration % 60);
        string gameInfo = $"{matchData.info.gameMode} • {minutes}:{seconds:D2}";
        ctx.DrawText(gameInfo, subFont, Color.FromRgb(140, 140, 140), new PointF(16, 48));
    }

    private void DrawTeamHeader(IImageProcessingContext ctx, string teamName, Color color, float yOffset)
    {
        var teamFont = _statsFamily.CreateFont(13);
        ctx.Fill(color.WithAlpha(0.15f), new RectangleF(0, yOffset, ImageWidth, TeamHeaderHeight));
        ctx.DrawText(teamName, teamFont, color, new PointF(10, yOffset + 9));
    }

    private void DrawTableHeaders(IImageProcessingContext ctx, float yOffset)
    {
        var headerFont = _statsFamily.CreateFont(10);
        Color c = Color.FromRgb(100, 100, 100);

        ctx.DrawText("CHAMPION", headerFont, c, new PointF(ColChampIcon, yOffset + 5));
        ctx.DrawText("ITEMS", headerFont, c, new PointF(ColItems, yOffset + 5));
        ctx.DrawText("KDA", headerFont, c, new PointF(ColKda, yOffset + 5));
        ctx.DrawText("CS", headerFont, c, new PointF(ColCs, yOffset + 5));
        ctx.DrawText("GOLD", headerFont, c, new PointF(ColGold, yOffset + 5));
        ctx.DrawText("DMG", headerFont, c, new PointF(ColDamage, yOffset + 5));
    }

    /// <summary>
    /// Draws a single player row with champion icon + level, name, packed items + trinket, and stats.
    /// </summary>
    private void DrawPlayerRow(
        IImageProcessingContext ctx, Participant player, float yOffset,
        PlayerAssets assets, bool isTrackedPlayer, bool isWinningTeam)
    {
        // ── Row background ──────────────────────────────────────────
        Color rowBg;
        if (isTrackedPlayer)
        {
            rowBg = isWinningTeam
                ? Color.FromRgb(35, 55, 75)
                : Color.FromRgb(65, 40, 45);
        }
        else
        {
            rowBg = isWinningTeam
                ? Color.FromRgb(22, 32, 42)
                : Color.FromRgb(38, 28, 33);
        }
        ctx.Fill(rowBg, new RectangleF(0, yOffset, ImageWidth, RowHeight));

        // Text colors
        Color textColor = isTrackedPlayer ? Color.FromRgb(255, 215, 0) : Color.White;
        Color mutedText = Color.FromRgb(160, 160, 160);
        Color goldColor = Color.FromRgb(200, 170, 90);
        Color dmgColor = Color.FromRgb(200, 100, 100);

        var nameFont = _statsFamily.CreateFont(12);
        var statsFont = _statsFamily.CreateFont(12);
        var levelFont = _statsFamily.CreateFont(10);

        float rowCenterY = yOffset + (RowHeight - ChampIconSize) / 2f;

        // ── Champion icon (32×32) ───────────────────────────────────
        DrawIcon(ctx, assets.ChampionPath, ColChampIcon, (int)rowCenterY, ChampIconSize);

        // ── Champion level badge ────────────────────────────────────
        DrawLevelBadge(ctx, player.level, levelFont, ColChampIcon, (int)(rowCenterY + ChampIconSize - 12));

        // ── Player name (truncated) ─────────────────────────────────
        string displayName = player.summonerName.Length > 12
            ? player.summonerName[..10] + ".."
            : player.summonerName;
        ctx.DrawText(displayName, nameFont, textColor, new PointF(ColName, yOffset + 15));

        // ── Items (packed left-to-right) + trinket ──────────────────
        float itemY = yOffset + (RowHeight - ItemIconSize) / 2f;
        DrawItems(ctx, assets.MainItemPaths, assets.TrinketPath, new PointF(ColItems, itemY));

        // ── KDA ─────────────────────────────────────────────────────
        string kda = $"{player.kills} / {player.deaths} / {player.assists}";
        ctx.DrawText(kda, statsFont, textColor, new PointF(ColKda, yOffset + 15));

        // ── CS ──────────────────────────────────────────────────────
        int totalCS = player.totalMinionsKilled + player.neutralMinionsKilled;
        ctx.DrawText(totalCS.ToString(), statsFont, mutedText, new PointF(ColCs, yOffset + 15));

        // ── Gold ────────────────────────────────────────────────────
        string gold = player.goldEarned >= 1000
            ? $"{player.goldEarned / 1000f:F1}k"
            : player.goldEarned.ToString();
        ctx.DrawText(gold, statsFont, goldColor, new PointF(ColGold, yOffset + 15));

        // ── Damage ──────────────────────────────────────────────────
        string damage = player.totalDamageDealtToChampions >= 1000
            ? $"{player.totalDamageDealtToChampions / 1000f:F1}k"
            : player.totalDamageDealtToChampions.ToString();
        ctx.DrawText(damage, statsFont, dmgColor, new PointF(ColDamage, yOffset + 15));
    }

    /// <summary>
    /// Renders a small level badge at the bottom-left corner of the champion icon.
    /// </summary>
    private void DrawLevelBadge(IImageProcessingContext ctx, int level, Font font, int x, int y)
    {
        const int badgeSize = 14;
        // Dark background circle/square for level number
        ctx.Fill(Color.FromRgb(0, 0, 0).WithAlpha(0.8f), new RectangleF(x, y, badgeSize, badgeSize));
        
        string levelStr = level.ToString();
        // Center the level text in the badge
        float textX = x + (badgeSize - levelStr.Length * 6) / 2f;
        float textY = y + 1;
        ctx.DrawText(levelStr, font, Color.FromRgb(200, 200, 200), new PointF(textX, textY));
    }

    /// <summary>
    /// Loads and draws a single icon at the specified position. Shows placeholder on failure.
    /// </summary>
    private void DrawIcon(IImageProcessingContext ctx, string path, int x, int y, int size)
    {
        if (string.IsNullOrEmpty(path))
        {
            ctx.Fill(Color.FromRgb(30, 35, 40), new RectangleF(x, y, size, size));
            return;
        }

        try
        {
            using var img = Image.Load(path);
            img.Mutate(i => i.Resize(size, size));
            ctx.DrawImage(img, new Point(x, y), 1f);
        }
        catch
        {
            ctx.Fill(Color.FromRgb(50, 50, 50), new RectangleF(x, y, size, size));
        }
    }

    /// <summary>
    /// Renders item icons packed left-to-right with no gaps between filled items.
    /// Empty slots are rendered after all filled items to maintain consistent row width.
    /// Trinket (ward) is always rendered at the end with a small gap.
    /// </summary>
    private void DrawItems(IImageProcessingContext ctx, List<string> packedItemPaths, string trinketPath, PointF startPos)
    {
        const int mainSlots = 7;  // item0-item5 + item7 (extra slot)

        // Draw packed non-empty items first (left-to-right, no gaps)
        for (int i = 0; i < packedItemPaths.Count && i < mainSlots; i++)
        {
            float currentX = startPos.X + (i * (ItemIconSize + ItemSpacing));
            DrawItemIcon(ctx, packedItemPaths[i], currentX, startPos.Y);
        }

        // Draw empty placeholder slots for remaining main positions
        for (int i = packedItemPaths.Count; i < mainSlots; i++)
        {
            float currentX = startPos.X + (i * (ItemIconSize + ItemSpacing));
            DrawEmptyItemSlot(ctx, currentX, startPos.Y);
        }

        // Draw trinket with gap after all main slots
        float trinketX = startPos.X + (mainSlots * (ItemIconSize + ItemSpacing)) + TrinketGap;
        if (!string.IsNullOrEmpty(trinketPath))
        {
            DrawItemIcon(ctx, trinketPath, trinketX, startPos.Y);
        }
        else
        {
            DrawEmptyItemSlot(ctx, trinketX, startPos.Y);
        }
    }

    /// <summary>
    /// Draws a single item icon at the given position.
    /// </summary>
    private void DrawItemIcon(IImageProcessingContext ctx, string path, float x, float y)
    {
        if (string.IsNullOrEmpty(path))
        {
            DrawEmptyItemSlot(ctx, x, y);
            return;
        }

        try
        {
            using var itemImg = Image.Load(path);
            itemImg.Mutate(i => i.Resize(ItemIconSize, ItemIconSize));
            ctx.DrawImage(itemImg, new Point((int)x, (int)y), 1f);
        }
        catch
        {
            ctx.Fill(Color.FromRgb(60, 30, 30), new RectangleF(x, y, ItemIconSize, ItemIconSize));
        }
    }

    /// <summary>
    /// Draws an empty item slot placeholder.
    /// </summary>
    private void DrawEmptyItemSlot(IImageProcessingContext ctx, float x, float y)
    {
        ctx.Fill(Color.FromRgb(18, 22, 28), new RectangleF(x, y, ItemIconSize, ItemIconSize));
        ctx.Draw(Color.FromRgb(30, 35, 40), 1f, new RectangleF(x, y, ItemIconSize, ItemIconSize));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _renderQueue.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}