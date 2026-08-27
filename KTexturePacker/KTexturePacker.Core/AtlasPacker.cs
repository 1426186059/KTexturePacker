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

        // 从「能容纳最大单张（含 padding）」的最小 2 的幂起步，页面紧贴内容、不无谓膨胀
        int start = 64;
        if (sprites.Count > 0)
            start = NextPowerOfTwo(sprites.Max(s => Math.Max(s.Bitmap.Width + pad * 2, s.Bitmap.Height + pad * 2)));
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

    /// <summary>
    /// 多页打包：当精灵在单张 MaxSize 图集里放不下时，自动分页（开新的一页继续塞），
    /// 直到所有精灵都放下或单张精灵本身超过 MaxSize（无法放下，留在最后一页的 Unplaced）。
    /// 返回每一页的 PackingResult（按页码索引 0,1,2…）。
    /// </summary>
    public static List<PackingResult> PackPages(IEnumerable<SpriteInput> inputs, PackerSettings settings)
    {
        int maxSize = Math.Max(64, settings.MaxSize);
        int pad = Math.Max(0, settings.Padding);

        var sorted = inputs
            .Where(s => s.Bitmap is not null && s.Bitmap.Width > 0 && s.Bitmap.Height > 0)
            .OrderByDescending(s => (long)s.Bitmap.Width * s.Bitmap.Height)
            .ToList();

        var pages = new List<PackingResult>();

        // 先把「单张尺寸(含 padding)超过 maxSize」的大图剥离出来，各自独占一页。
        // 这样它们不会把整本图集撑成一张超大页、把其余小图也卷进去
        //（旧逻辑会因此产生 4096 巨页 + 大量空白，并被预览切成几十页）。
        // 这些大图页允许突破 maxSize，但尺寸紧贴实际内容（必要时用非 2 的幂），避免无谓翻倍膨胀。
        var oversized = sorted
            .Where(s => Math.Max(s.Bitmap.Width, s.Bitmap.Height) + pad * 2 > maxSize)
            .ToList();
        foreach (var big in oversized)
        {
            int dim = Math.Max(big.Bitmap.Width, big.Bitmap.Height);
            int need = (int)Math.Ceiling(dim + pad * 2.0);
            int pot = NextPowerOfTwo(dim);
            int size = need <= pot ? pot : need;
            pages.Add(TryPack(new List<SpriteInput> { big }, size, pad, settings));
        }

        // 剩余小图严格按 maxSize 分页（不再突破 maxSize），由 MaxRects 紧凑填充
        var remaining = sorted.Except(oversized).ToList();
        while (remaining.Count > 0)
        {
            int maxSingle = remaining.Max(s => Math.Max(s.Bitmap.Width, s.Bitmap.Height)) + pad * 2;
            // pageMax 固定为 maxSize（封顶）；start 取「能容纳最大一张（含 padding）」的最小 2 的幂，循环逐步翻倍找到能放下全部的最小页。
            int pageMax = maxSize;

            int start = Math.Min(NextPowerOfTwo(maxSingle), pageMax);

            PackingResult? best = null;
            int size = start;
            while (size <= pageMax)
            {
                var attempt = TryPack(remaining, size, pad, settings);
                if (attempt.Unplaced.Count == 0)
                {
                    best = attempt;
                    break;
                }
                // 用最大（最新）的尝试：它放下的精灵最多
                best = attempt;
                size *= 2;
            }

            var page = best ?? TryPack(remaining, pageMax, pad, settings);
            pages.Add(page);
            remaining = page.Unplaced;

            // 防止死循环：当前页一张都没放下（如某张精灵本身就超过 MaxSize）
            if (page.Sprites.Count == 0)
                break;
        }

        // 非末页的 Unplaced 只是「留给下一页」的接力，并非真正放不下；
        // 只保留末页的 Unplaced 表示真正无法放入的精灵。
        for (int i = 0; i < pages.Count - 1; i++)
            pages[i].Unplaced.Clear();

        return pages;
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
