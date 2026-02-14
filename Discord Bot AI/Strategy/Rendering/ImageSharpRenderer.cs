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
/// 
/// Item slot mapping (Riot API match-v5):
///   item0–item5    : 6 main inventory slots
///   item6          : trinket (ward)
///   roleBoundItem  : role quest slot (boots for ADC/Support after quest completion)
/// 
/// Render order per row:
///   [Champion icon + level] [Name] [item0–item5 packed left] [trinket] [role item] [KDA] [CS] [Gold] [DMG]
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
    private const int ColName = 56;
    private const int ColItems = 170;
    private const int ColKda = 430;
    private const int ColCs = 520;
    private const int ColGold = 580;
    private const int ColDamage = 660;

    // Icon sizes
    private const int ChampIconSize = 32;
    private const int ItemIconSize = 24;
    private const int ItemSpacing = 2;
    private const int SeparatorGap = 5;

    // Item slot counts
    private const int MainSlots = 6;      // item0–item5

    // Role order for sorting players (TOP, JUNGLE, MID, ADC, SUPPORT)
    private static readonly Dictionary<string, int> RoleOrder = new(StringComparer.OrdinalIgnoreCase)
    {
        { "TOP", 0 },
        { "JUNGLE", 1 },
        { "MIDDLE", 2 },
        { "BOTTOM", 3 },
        { "UTILITY", 4 }
    };

    public ImageSharpRenderer(RiotImageCacheService imageCache)
    {
        _imageCache = imageCache;
        _headingFamily = _fontCollection.Add("Assets/Fonts/Cinzel static/Cinzel-Bold.ttf");
        _statsFamily = _fontCollection.Add("Assets/Fonts/Roboto static/RobotoCondensed-Bold.ttf");
    }

    // ════════════════════════════════════════════════════════════════════
    //  Public API
    // ════════════════════════════════════════════════════════════════════

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

    // ════════════════════════════════════════════════════════════════════
    //  Internal rendering pipeline
    // ════════════════════════════════════════════════════════════════════

    private async Task<Stream> RenderSummaryInternalAsync(RiotAccount account, MatchData matchData, CancellationToken cancellationToken)
    {
        var me = matchData.info.participants.FirstOrDefault(p => p.puuid == account.puuid)
                 ?? throw new Exception("Player not found in match data");

        var allParticipants = matchData.info.participants;
        var winningTeam = SortByRole(allParticipants.Where(p => p.win).ToList());
        var losingTeam = SortByRole(allParticipants.Where(p => !p.win).ToList());

        cancellationToken.ThrowIfCancellationRequested();

        // ── Diagnostic: log item slots ────────────
        foreach (var p in allParticipants)
        {
            string displayName = !string.IsNullOrEmpty(p.riotIdGameName) ? p.riotIdGameName : p.summonerName ?? "?";
            Log.Debug("[Items] {Name} pos={Pos} items=[{I0},{I1},{I2},{I3},{I4},{I5}] trinket={I6} roleItem={RoleItem}",
                displayName, p.teamPosition ?? "?",
                p.item0, p.item1, p.item2, p.item3, p.item4, p.item5, p.item6, p.roleBoundItem);
        }

        var playerAssets = await LoadAllPlayerAssetsAsync(allParticipants, cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();

        int team1Height = TeamHeaderHeight + ColumnHeaderHeight + (winningTeam.Count * RowHeight);
        int team2Height = TeamHeaderHeight + ColumnHeaderHeight + (losingTeam.Count * RowHeight);
        int imageHeight = HeaderHeight + team1Height + TeamSpacing + team2Height + BottomPadding;

        using Image<Rgba32> image = new(ImageWidth, imageHeight);

        image.Mutate(ctx =>
        {
            DrawBackground(ctx);
            DrawHeader(ctx, me, matchData);

            float y = HeaderHeight;

            y = DrawTeamBlock(ctx, winningTeam, playerAssets, account.puuid, y,
                              "VICTORY", Color.FromRgb(70, 130, 180), isWinningTeam: true);
            y += TeamSpacing;
            DrawTeamBlock(ctx, losingTeam, playerAssets, account.puuid, y,
                          "DEFEAT", Color.FromRgb(180, 70, 70), isWinningTeam: false);
        });

        var ms = new MemoryStream();
        await image.SaveAsPngAsync(ms, cancellationToken);
        ms.Position = 0;

        Log.Debug("Rendered full match summary for {PlayerName} with {PlayerCount} players",
                  account.gameName, allParticipants.Count);
        return ms;
    }

    private float DrawTeamBlock(IImageProcessingContext ctx, List<Participant> team,
                                Dictionary<string, PlayerAssets> assets, string trackedPuuid,
                                float yOffset, string label, Color color, bool isWinningTeam)
    {
        DrawTeamHeader(ctx, label, color, yOffset);
        yOffset += TeamHeaderHeight;
        DrawTableHeaders(ctx, yOffset);
        yOffset += ColumnHeaderHeight;

        foreach (var player in team)
        {
            var a = assets[player.puuid];
            bool tracked = player.puuid == trackedPuuid;
            DrawPlayerRow(ctx, player, yOffset, a, tracked, isWinningTeam);
            yOffset += RowHeight;
        }

        return yOffset;
    }

    // ════════════════════════════════════════════════════════════════════
    //  Asset loading
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Loads champion and item icons for all participants in parallel.
    /// 
    /// Riot API item mapping:
    ///   item0–item5    → main inventory (6 slots, packed left, empties filtered out)
    ///   item6          → trinket / ward
    ///   roleBoundItem  → role quest slot (boots for ADC/Support after quest completion)
    /// </summary>
    private async Task<Dictionary<string, PlayerAssets>> LoadAllPlayerAssetsAsync(
        List<Participant> participants, CancellationToken cancellationToken)
    {
        var tasks = participants.Select(async p =>
        {
            string championPath = await _imageCache.GetChampionIconAsync(p.championName, cancellationToken);

            // Main slots: item0–item5 → pack non-empty items to the left
            int[] mainIds = { p.item0, p.item1, p.item2, p.item3, p.item4, p.item5 };
            var nonEmptyMainIds = mainIds.Where(id => id != 0).ToList();

            var mainTasks = nonEmptyMainIds.Select(id => _imageCache.GetItemIconAsync(id, cancellationToken));
            var mainPaths = (await Task.WhenAll(mainTasks)).ToList();

            // Trinket: item6
            string trinketPath = p.item6 != 0
                ? await _imageCache.GetItemIconAsync(p.item6, cancellationToken)
                : "";

            // Role quest slot: roleBoundItem (boots for ADC/Support after quest)
            string roleItemPath = p.roleBoundItem != 0
                ? await _imageCache.GetItemIconAsync(p.roleBoundItem, cancellationToken)
                : "";

            return new PlayerAssets
            {
                Puuid = p.puuid,
                ChampionPath = championPath,
                MainItemPaths = mainPaths,
                TrinketPath = trinketPath,
                RoleItemPath = roleItemPath
            };
        });

        var allAssets = await Task.WhenAll(tasks);
        return allAssets.ToDictionary(a => a.Puuid);
    }

    private class PlayerAssets
    {
        public string Puuid { get; set; } = "";
        public string ChampionPath { get; set; } = "";
        public List<string> MainItemPaths { get; set; } = new();
        public string TrinketPath { get; set; } = "";
        public string RoleItemPath { get; set; } = "";
    }

    // ════════════════════════════════════════════════════════════════════
    //  Helpers
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Sorts participants by their team position: TOP, JUNGLE, MIDDLE, BOTTOM, UTILITY.
    /// If position is unknown, places the player at the end.
    /// </summary>
    private static List<Participant> SortByRole(List<Participant> participants)
    {
        return participants
            .OrderBy(p => RoleOrder.TryGetValue(p.teamPosition ?? string.Empty, out var order) ? order : 99)
            .ToList();
    }

    // ════════════════════════════════════════════════════════════════════
    //  Drawing: chrome
    // ════════════════════════════════════════════════════════════════════

    private void DrawBackground(IImageProcessingContext ctx)
        => ctx.Fill(Color.FromRgb(10, 20, 25));

    private void DrawHeader(IImageProcessingContext ctx, Participant me, MatchData matchData)
    {
        var titleFont = _headingFamily.CreateFont(28);
        var subFont = _statsFamily.CreateFont(13);

        string title = me.win ? "VICTORY" : "DEFEAT";
        Color titleColor = me.win ? Color.FromRgb(70, 130, 180) : Color.FromRgb(180, 70, 70);
        ctx.DrawText(title, titleFont, titleColor, new PointF(16, 12));

        int min = (int)(matchData.info.gameDuration / 60);
        int sec = (int)(matchData.info.gameDuration % 60);
        ctx.DrawText($"{matchData.info.gameMode} • {min}:{sec:D2}",
                     subFont, Color.FromRgb(140, 140, 140), new PointF(16, 48));
    }

    private void DrawTeamHeader(IImageProcessingContext ctx, string label, Color color, float y)
    {
        var font = _statsFamily.CreateFont(13);
        ctx.Fill(color.WithAlpha(0.15f), new RectangleF(0, y, ImageWidth, TeamHeaderHeight));
        ctx.DrawText(label, font, color, new PointF(10, y + 9));
    }

    private void DrawTableHeaders(IImageProcessingContext ctx, float y)
    {
        var font = _statsFamily.CreateFont(10);
        Color c = Color.FromRgb(100, 100, 100);

        ctx.DrawText("CHAMPION", font, c, new PointF(ColChampIcon, y + 5));
        ctx.DrawText("ITEMS", font, c, new PointF(ColItems, y + 5));
        ctx.DrawText("KDA", font, c, new PointF(ColKda, y + 5));
        ctx.DrawText("CS", font, c, new PointF(ColCs, y + 5));
        ctx.DrawText("GOLD", font, c, new PointF(ColGold, y + 5));
        ctx.DrawText("DMG", font, c, new PointF(ColDamage, y + 5));
    }

    // ════════════════════════════════════════════════════════════════════
    //  Drawing: player row
    // ════════════════════════════════════════════════════════════════════

    private void DrawPlayerRow(IImageProcessingContext ctx, Participant player, float y,
                               PlayerAssets assets, bool isTracked, bool isWinningTeam)
    {
        Color rowBg = (isTracked, isWinningTeam) switch
        {
            (true, true) => Color.FromRgb(35, 55, 75),
            (true, false) => Color.FromRgb(65, 40, 45),
            (false, true) => Color.FromRgb(22, 32, 42),
            (false, false) => Color.FromRgb(38, 28, 33),
        };
        ctx.Fill(rowBg, new RectangleF(0, y, ImageWidth, RowHeight));

        Color text = isTracked ? Color.FromRgb(255, 215, 0) : Color.White;
        Color muted = Color.FromRgb(160, 160, 160);
        Color gold = Color.FromRgb(200, 170, 90);
        Color dmg = Color.FromRgb(200, 100, 100);

        var nameFont = _statsFamily.CreateFont(12);
        var statsFont = _statsFamily.CreateFont(12);
        var levelFont = _statsFamily.CreateFont(10);

        float iconY = y + (RowHeight - ChampIconSize) / 2f;
        float textY = y + 15;

        // ── Champion icon ───────────────────────────────────────────
        DrawIcon(ctx, assets.ChampionPath, ColChampIcon, (int)iconY, ChampIconSize);

        // ── Level badge (bottom-left of champion icon) ──────────────
        DrawLevelBadge(ctx, player.champLevel, levelFont, ColChampIcon, (int)(iconY + ChampIconSize - 12));

        // ── Player name ─────────────────────────────────────────────
        string displayName = !string.IsNullOrEmpty(player.riotIdGameName)
            ? player.riotIdGameName
            : !string.IsNullOrEmpty(player.summonerName) ? player.summonerName : "Unknown";
        string name = displayName.Length > 12
            ? displayName[..10] + ".."
            : displayName;
        ctx.DrawText(name, nameFont, text, new PointF(ColName, textY));

        // ── Items: [6 main packed] [gap] [trinket] [gap] [quest] ───
        float itemY = y + (RowHeight - ItemIconSize) / 2f;
        DrawAllItems(ctx, assets, new PointF(ColItems, itemY));

        // ── Stats ───────────────────────────────────────────────────
        ctx.DrawText($"{player.kills} / {player.deaths} / {player.assists}",
                     statsFont, text, new PointF(ColKda, textY));

        int cs = player.totalMinionsKilled + player.neutralMinionsKilled;
        ctx.DrawText(cs.ToString(), statsFont, muted, new PointF(ColCs, textY));

        string goldStr = player.goldEarned >= 1000
            ? $"{player.goldEarned / 1000f:F1}k" : player.goldEarned.ToString();
        ctx.DrawText(goldStr, statsFont, gold, new PointF(ColGold, textY));

        string dmgStr = player.totalDamageDealtToChampions >= 1000
            ? $"{player.totalDamageDealtToChampions / 1000f:F1}k"
            : player.totalDamageDealtToChampions.ToString();
        ctx.DrawText(dmgStr, statsFont, dmg, new PointF(ColDamage, textY));
    }

    // ════════════════════════════════════════════════════════════════════
    //  Drawing: items
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Renders the full item bar for one player:
    ///   [item0–item5 packed left, empties after] [gap] [trinket/item6] [role item]
    /// 
    /// Layout matches the LoL in-game scoreboard:
    ///  - 6 main slots always shown (filled packed left, empties right)
    ///  - trinket separated by a small gap
    ///  - role quest item (roleBoundItem) directly after trinket
    /// </summary>
    private void DrawAllItems(IImageProcessingContext ctx, PlayerAssets assets, PointF start)
    {
        float x = start.X;
        float iconStep = ItemIconSize + ItemSpacing;

        // ── 6 main item slots (item0–item5) ─────────────────────────
        int filled = Math.Min(assets.MainItemPaths.Count, MainSlots);
        for (int i = 0; i < filled; i++)
            DrawItemIcon(ctx, assets.MainItemPaths[i], x + (i * iconStep), start.Y);
        for (int i = filled; i < MainSlots; i++)
            DrawEmptyItemSlot(ctx, x + (i * iconStep), start.Y);

        // ── Trinket (item6) ─────────────────────────────────────────
        float trinketX = x + (MainSlots * iconStep) + SeparatorGap;
        if (!string.IsNullOrEmpty(assets.TrinketPath))
            DrawItemIcon(ctx, assets.TrinketPath, trinketX, start.Y);
        else
            DrawEmptyItemSlot(ctx, trinketX, start.Y);

        // ── Role quest item (roleBoundItem) ─────────────────────────
        float roleItemX = trinketX + iconStep;
        if (!string.IsNullOrEmpty(assets.RoleItemPath))
            DrawItemIcon(ctx, assets.RoleItemPath, roleItemX, start.Y);
    }

    // ════════════════════════════════════════════════════════════════════
    //  Drawing: primitives
    // ════════════════════════════════════════════════════════════════════

    private void DrawLevelBadge(IImageProcessingContext ctx, int level, Font font, int x, int y)
    {
        const int badgeW = 14;
        const int badgeH = 12;
        ctx.Fill(Color.FromRgb(0, 0, 0).WithAlpha(0.85f), new RectangleF(x, y, badgeW, badgeH));
        string text = level.ToString();
        float tx = x + (badgeW - text.Length * 6) / 2f;
        ctx.DrawText(text, font, Color.FromRgb(200, 200, 200), new PointF(tx, y + 1));
    }

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

    private void DrawItemIcon(IImageProcessingContext ctx, string path, float x, float y)
    {
        if (string.IsNullOrEmpty(path))
        {
            DrawEmptyItemSlot(ctx, x, y);
            return;
        }

        try
        {
            using var img = Image.Load(path);
            img.Mutate(i => i.Resize(ItemIconSize, ItemIconSize));
            ctx.DrawImage(img, new Point((int)x, (int)y), 1f);
        }
        catch
        {
            ctx.Fill(Color.FromRgb(60, 30, 30), new RectangleF(x, y, ItemIconSize, ItemIconSize));
        }
    }

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