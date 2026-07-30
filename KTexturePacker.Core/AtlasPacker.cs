using System;
using System.Collections.Generic;
using System.Linq;
using SkiaSharp;

namespace KTexturePacker.Core;

/// <summary>
/// 待打包的一张精灵（名称 + 位图）。
/// </summary>
public sealed class SpriteInput
{
    public string Name { get; }
    public SKBitmap Bitmap { get; }

    public SpriteInput(string name, SKBitmap bitmap)
    {
        Name = name;
        Bitmap = bitmap;
    }
}

/// <summary>
/// 打包后的一张精灵在图集中的位置信息。
/// </summary>
public sealed class PackedSprite
{
    public string Name { get; init; } = "";
    public SKBitmap Bitmap { get; init; } = null!;
    public int X { get; init; }
    public int Y { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public bool Rotated { get; init; }
    public int SourceWidth { get; init; }
    public int SourceHeight { get; init; }
}

/// <summary>
/// 一次打包的结果。
/// </summary>
public sealed class PackingResult
{
    public int AtlasWidth { get; init; }
    public int AtlasHeight { get; init; }
    public List<PackedSprite> Sprites { get; init; } = new();
    public List<SpriteInput> Unplaced { get; init; } = new();
}

/// <summary>
/// 图集打包器：把多张精灵按 MaxRects 算法排布，并用 SkiaSharp 合成最终大图。
/// </summary>
public static class AtlasPacker
{
    /// <summary>
    /// 打包一组精灵。会自动从能容纳的最小 2 的幂尺寸开始试探，选择第一个能放下全部精灵的尺寸；
    /// 若到 MaxSize 仍放不下，则返回 MaxSize 下的“尽力而为”结果（放不下的记入 Unplaced）。
    /// </summary>
    public static PackingResult Pack(IEnumerable<SpriteInput> inputs, PackerSettings settings)
    {
        var sprites = inputs
            .Where(s => s.Bitmap is not null && s.Bitmap.Width > 0 && s.Bitmap.Height > 0)
            .OrderByDescending(s => (long)s.Bitmap.Width * s.Bitmap.Height)
            .ToList();

        int maxSize = Math.Max(64, settings.MaxSize);
        int pad = Math.Max(0, settings.Padding);

        int start = 64;
        if (sprites.Count > 0)
            start = Math.Max(start, NextPowerOfTwo(sprites.Max(s => Math.Max(s.Bitmap.Width, s.Bitmap.Height))));
        start = Math.Min(start, maxSize);

        PackingResult? best = null;
        int size = start;
        while (size <= maxSize)
        {
            var attempt = TryPack(sprites, size, pad, settings);
            if (attempt.Unplaced.Count == 0)
            {
                best = attempt;
                break;
            }

            best ??= attempt;
            size *= 2;
        }

        return best ?? TryPack(sprites, maxSize, pad, settings);
    }

    private static PackingResult TryPack(List<SpriteInput> sprites, int size, int pad, PackerSettings settings)
    {
        var packer = new MaxRectsPacker(size, size, settings.AllowRotation, settings.Algorithm);
        var placed = new List<PackedSprite>();
        var unplaced = new List<SpriteInput>();

        foreach (var s in sprites)
        {
            int w0 = s.Bitmap.Width;
            int h0 = s.Bitmap.Height;
            int pw = w0 + pad * 2;
            int ph = h0 + pad * 2;

            var r = packer.Insert(pw, ph, out bool rotated);
            if (r.Width == 0 && r.Height == 0)
            {
                unplaced.Add(s);
                continue;
            }

            int drawX = r.X + pad;
            int drawY = r.Y + pad;
            int drawW = rotated ? h0 : w0;
            int drawH = rotated ? w0 : h0;

            placed.Add(new PackedSprite
            {
                Name = s.Name,
                Bitmap = s.Bitmap,
                X = drawX,
                Y = drawY,
                Width = drawW,
                Height = drawH,
                Rotated = rotated,
                SourceWidth = w0,
                SourceHeight = h0,
            });
        }

        return new PackingResult
        {
            AtlasWidth = size,
            AtlasHeight = size,
            Sprites = placed,
            Unplaced = unplaced,
        };
    }

    /// <summary>
    /// 根据打包结果，把精灵绘制到一张透明背景的大位图上。
    /// </summary>
    public static SKBitmap RenderAtlas(PackingResult result)
    {
        var atlas = new SKBitmap(result.AtlasWidth, result.AtlasHeight);
        using var canvas = new SKCanvas(atlas);
        canvas.Clear(SKColors.Transparent);

        foreach (var p in result.Sprites)
        {
            var bmp = p.Bitmap;
            int w0 = bmp.Width;
            int h0 = bmp.Height;

            if (p.Rotated)
            {
                float cx = p.X + p.Width / 2f;
                float cy = p.Y + p.Height / 2f;
                canvas.Save();
                canvas.RotateDegrees(90, cx, cy);
                canvas.DrawBitmap(bmp, cx - w0 / 2f, cy - h0 / 2f, new SKSamplingOptions(SKFilterMode.Linear));
                canvas.Restore();
            }
            else
            {
                canvas.DrawBitmap(bmp, p.X, p.Y, new SKSamplingOptions(SKFilterMode.Linear));
            }
        }

        return atlas;
    }

    private static int NextPowerOfTwo(int v)
    {
        int p = 1;
        while (p < v)
            p <<= 1;
        return p;
    }
}
