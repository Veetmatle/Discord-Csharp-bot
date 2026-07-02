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
/// Renders TFT match summary images showing all 8 players sorted by placement.
/// Layout: placement badge, player name, level, traits, units with items, and stats.
/// </summary>
public class TftImageSharpRenderer : ITftSummaryRenderer, IDisposable
{
    private readonly FontCollection _fontCollection = new();
    private readonly FontFamily _headingFamily;
    private readonly FontFamily _statsFamily;
    private readonly RiotImageCacheService _imageCache;

    private readonly SemaphoreSlim _renderQueue = new(2, 2);
    private static readonly TimeSpan RenderTimeout = TimeSpan.FromSeconds(30);
    private bool _disposed;

    private const int ImageWidth = 800;
    private const int HeaderHeight = 70;
    private const int PlayerBlockHeight = 90;
    private const int PlayerSpacing = 4;
    private const int BottomPadding = 16;

    private const int ColPlacement = 10;
    private const int ColName = 50;
    private const int ColTraits = 200;
    private const int ColUnits = 420;
    private const int ColStats = 700;

    private const int UnitIconSize = 28;
    private const int UnitSpacing = 2;

    private static readonly Color[] PlacementColors =
    {
        Color.FromRgb(255, 215, 0),
        Color.FromRgb(192, 192, 192),
        Color.FromRgb(205, 127, 50),
        Color.FromRgb(100, 160, 100),
        Color.FromRgb(120, 120, 140),
        Color.FromRgb(120, 120, 140),
        Color.FromRgb(160, 80, 80),
        Color.FromRgb(160, 80, 80),
    };

    public TftImageSharpRenderer(RiotImageCacheService imageCache)
    {
        _imageCache = imageCache;
        _headingFamily = _fontCollection.Add("Assets/Fonts/Cinzel static/Cinzel-Bold.ttf");
        _statsFamily = _fontCollection.Add("Assets/Fonts/Roboto static/RobotoCondensed-Bold.ttf");
    }

    /// <summary>
    /// Renders a full TFT match summary showing all 8 players sorted by placement.
    /// </summary>
    public async Task<Stream> RenderTftSummaryAsync(RiotAccount account, TftMatchData matchData, CancellationToken cancellationToken = default)
    {
        using var timeoutCts = new CancellationTokenSource(RenderTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        if (!await _renderQueue.WaitAsync(RenderTimeout, linkedCts.Token))
        {
            Log.Warning("TFT render queue full, request timed out");
            throw new TimeoutException("Render queue is full. Please try again later.");
        }

        try
        {
            return await RenderInternalAsync(account, matchData, linkedCts.Token);
        }
        finally
        {
            _renderQueue.Release();
        }
    }

    private async Task<Stream> RenderInternalAsync(RiotAccount account, TftMatchData matchData, CancellationToken ct)
    {
        var me = matchData.info.participants.FirstOrDefault(p => p.puuid == account.puuid)
                 ?? throw new Exception("Player not found in TFT match data");

        var sorted = matchData.info.participants.OrderBy(p => p.placement).ToList();

        ct.ThrowIfCancellationRequested();

        var iconPaths = await LoadUnitIconsAsync(sorted, ct);
        var scaledIcons = PreScaleIcons(iconPaths);

        int playerCount = sorted.Count;
        int imageHeight = HeaderHeight + (playerCount * (PlayerBlockHeight + PlayerSpacing)) + BottomPadding;

        using Image<Rgba32> image = new(ImageWidth, imageHeight);

        image.Mutate(ctx =>
        {
            ctx.Fill(Color.FromRgb(10, 20, 25));
            DrawHeader(ctx, me, matchData);

            float y = HeaderHeight;
            foreach (var player in sorted)
            {
                bool isTracked = player.puuid == account.puuid;
                DrawPlayerBlock(ctx, player, y, scaledIcons, isTracked);
                y += PlayerBlockHeight + PlayerSpacing;
            }
        });

        foreach (var icon in scaledIcons.Values)
        {
            icon.Dispose();
        }

        var ms = new MemoryStream();
        await image.SaveAsPngAsync(ms, ct);
        ms.Position = 0;

        Log.Debug("Rendered TFT summary for {Player}, placement #{Place}", account.gameName, me.placement);
        return ms;
    }

    /// <summary>
    /// Pre-loads and pre-scales all unique unit icons to avoid repeated disk I/O and CPU-heavy resizes.
    /// </summary>
    private Dictionary<string, Image> PreScaleIcons(Dictionary<string, string> iconPaths)
    {
        var result = new Dictionary<string, Image>();
        foreach (var (characterId, path) in iconPaths)
        {
            if (string.IsNullOrEmpty(path)) continue;
            try
            {
                var img = Image.Load(path);
                img.Mutate(i => i.Resize(UnitIconSize, UnitIconSize));
                result[characterId] = img;
            }
            catch (Exception ex)
            {
                Log.Debug("Failed to pre-scale icon for {UnitId}: {Error}", characterId, ex.Message);
            }
        }
        return result;
    }

    private void DrawHeader(IImageProcessingContext ctx, TftParticipant me, TftMatchData match)
    {
        var titleFont = _headingFamily.CreateFont(24);
        var subFont = _statsFamily.CreateFont(13);

        bool topFour = me.placement <= 4;
        string title = topFour ? $"#{me.placement} - TOP 4" : $"#{me.placement} - ELIMINATED";
        Color titleColor = topFour ? Color.FromRgb(70, 130, 180) : Color.FromRgb(180, 70, 70);

        ctx.DrawText(title, titleFont, titleColor, new PointF(16, 10));

        int min = (int)(match.info.game_length / 60);
        int sec = (int)(match.info.game_length % 60);
        string setName = !string.IsNullOrEmpty(match.info.tft_set_core_name)
            ? match.info.tft_set_core_name
            : $"Set {match.info.tft_set_number}";
        ctx.DrawText($"TFT • {setName} • {min}:{sec:D2}",
            subFont, Color.FromRgb(140, 140, 140), new PointF(16, 42));
    }

    private void DrawPlayerBlock(IImageProcessingContext ctx, TftParticipant player, float y,
        Dictionary<string, Image> scaledIcons, bool isTracked)
    {
        int placementIdx = Math.Clamp(player.placement - 1, 0, PlacementColors.Length - 1);
        Color placementColor = PlacementColors[placementIdx];

        Color rowBg = isTracked
            ? Color.FromRgb(35, 55, 75)
            : Color.FromRgb(22, 32, 42);
        ctx.Fill(rowBg, new RectangleF(0, y, ImageWidth, PlayerBlockHeight));

        ctx.Fill(placementColor.WithAlpha(0.3f), new RectangleF(0, y, 4, PlayerBlockHeight));

        var placementFont = _headingFamily.CreateFont(22);
        var nameFont = _statsFamily.CreateFont(13);
        var statsFont = _statsFamily.CreateFont(11);
        var traitFont = _statsFamily.CreateFont(10);

        Color textColor = isTracked ? Color.FromRgb(255, 215, 0) : Color.White;
        Color muted = Color.FromRgb(140, 140, 140);

        ctx.DrawText($"#{player.placement}", placementFont, placementColor,
            new PointF(ColPlacement, y + 30));

        string displayName = !string.IsNullOrEmpty(player.riotIdGameName)
            ? player.riotIdGameName : "Unknown";
        string name = displayName.Length > 16 ? displayName[..14] + ".." : displayName;
        ctx.DrawText(name, nameFont, textColor, new PointF(ColName, y + 8));

        ctx.DrawText($"Lv {player.level}", statsFont, muted, new PointF(ColName, y + 26));

        DrawTraits(ctx, player.traits, ColTraits, y + 6, traitFont);

        DrawUnits(ctx, player.units, scaledIcons, ColUnits, y + 8);

        string statsText = $"{player.players_eliminated} elim • {player.gold_left}g • {player.total_damage_to_players} dmg";
        ctx.DrawText(statsText, statsFont, muted, new PointF(ColStats, y + 8));

        DrawAugments(ctx, player.augments, y + 50, statsFont, muted);
    }

    private void DrawTraits(IImageProcessingContext ctx, List<TftTrait> traits, float x, float y, Font font)
    {
        var activeTraits = traits
            .Where(t => t.tier_current > 0 && t.style > 0)
            .OrderByDescending(t => t.style)
            .ThenByDescending(t => t.num_units)
            .Take(6)
            .ToList();

        float currentY = y;
        Color[] traitTierColors =
        {
            Color.FromRgb(120, 120, 120),
            Color.FromRgb(166, 124, 82),
            Color.FromRgb(192, 192, 192),
            Color.FromRgb(255, 215, 0),
            Color.FromRgb(50, 200, 200),
        };

        foreach (var trait in activeTraits)
        {
            int styleIdx = Math.Clamp(trait.style, 0, traitTierColors.Length - 1);
            Color color = traitTierColors[styleIdx];

            string traitName = FormatTraitName(trait.name);
            string label = $"{traitName} ({trait.num_units})";
            ctx.DrawText(label, font, color, new PointF(x, currentY));
            currentY += 13;
        }
    }

    private void DrawUnits(IImageProcessingContext ctx, List<TftUnit> units, Dictionary<string, Image> scaledIcons,
        float x, float y)
    {
        var sorted = units.OrderByDescending(u => u.tier).ThenByDescending(u => u.rarity).ToList();
        float currentX = x;

        Color[] rarityColors =
        {
            Color.FromRgb(120, 120, 120),
            Color.FromRgb(0, 128, 0),
            Color.FromRgb(0, 100, 200),
            Color.FromRgb(160, 32, 240),
            Color.FromRgb(255, 165, 0),
        };

        foreach (var unit in sorted)
        {
            if (scaledIcons.TryGetValue(unit.character_id, out var icon))
            {
                ctx.DrawImage(icon, new Point((int)currentX, (int)y), 1f);
            }
            else
            {
                ctx.Fill(Color.FromRgb(30, 35, 40), new RectangleF(currentX, y, UnitIconSize, UnitIconSize));
            }

            int rarityIdx = Math.Clamp(unit.rarity, 0, rarityColors.Length - 1);
            ctx.Fill(rarityColors[rarityIdx], new RectangleF(currentX, y + UnitIconSize, UnitIconSize, 3));

            if (unit.tier > 1)
            {
                string stars = new('*', Math.Min(unit.tier, 3));
                var starFont = _statsFamily.CreateFont(8);
                ctx.DrawText(stars, starFont, Color.FromRgb(255, 215, 0),
                    new PointF(currentX, y - 8));
            }

            currentX += UnitIconSize + UnitSpacing;
        }
    }

    private void DrawAugments(IImageProcessingContext ctx, List<string> augments, float y, Font font, Color color)
    {
        if (augments.Count == 0) return;

        var augmentNames = augments.Select(FormatTraitName).ToList();
        string augmentText = string.Join(" | ", augmentNames);
        ctx.DrawText($"Augments: {augmentText}", font, color, new PointF(ColName, y));
    }

    private async Task<Dictionary<string, string>> LoadUnitIconsAsync(List<TftParticipant> participants, CancellationToken ct)
    {
        var allUnitIds = participants
            .SelectMany(p => p.units)
            .Select(u => u.character_id)
            .Distinct()
            .ToList();

        var result = new Dictionary<string, string>();

        foreach (var characterId in allUnitIds)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                string champName = ExtractChampionName(characterId);
                string path = await _imageCache.GetChampionIconAsync(champName, ct);
                result[characterId] = path;
            }
            catch (Exception ex)
            {
                Log.Debug("Could not load icon for TFT unit {UnitId}: {Error}", characterId, ex.Message);
                result[characterId] = "";
            }
        }

        return result;
    }

    private static string ExtractChampionName(string characterId)
    {
        if (string.IsNullOrEmpty(characterId)) return characterId;

        int lastUnderscore = characterId.LastIndexOf('_');
        return lastUnderscore >= 0 && lastUnderscore < characterId.Length - 1
            ? characterId[(lastUnderscore + 1)..]
            : characterId;
    }

    private static string FormatTraitName(string rawName)
    {
        if (string.IsNullOrEmpty(rawName)) return rawName;

        int lastUnderscore = rawName.LastIndexOf('_');
        string name = lastUnderscore >= 0 && lastUnderscore < rawName.Length - 1
            ? rawName[(lastUnderscore + 1)..]
            : rawName;

        if (name.StartsWith("Set", StringComparison.OrdinalIgnoreCase) && name.Length > 4)
        {
            int idx = name.IndexOf('_');
            if (idx >= 0 && idx < name.Length - 1)
                name = name[(idx + 1)..];
        }

        return name;
    }


    public void Dispose()
    {
        if (_disposed) return;
        _renderQueue.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
