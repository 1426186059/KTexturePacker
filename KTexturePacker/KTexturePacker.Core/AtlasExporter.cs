using System.Collections.Generic;
using System.Text;
using System.Text.Json.Nodes;
using KTexturePacker.Core.JsonFormat;

namespace KTexturePacker.Core;

/// <summary>
/// 描述文件（JSON）导出格式。只有 PixiJS 需要专属的 Spritesheet 官方结构；
/// 其余引擎（Unity / UE5 / MonoGame / 通用）共用 KTexturePacker 通用格式，供 AtlasParser 解析。
/// </summary>
public enum AtlasFormat
{
    /// <summary>KTexturePacker 通用格式（pages/regions），后缀 .atlas.txt。Unity / UE5 / MonoGame 端解析器均消费此格式。</summary>
    Generic,

    /// <summary>PixiJS v8 Spritesheet 官方格式（frames/meta/animations），默认后缀 .atlas.json。PixiJS 的 Assets.load 可直接识别，无需自定义 loader。</summary>
    PixiJS,
}

/// <summary>
/// 把打包结果导出为描述文件（libGDX .atlas 文本格式 / 各引擎 JSON 格式）。
/// </summary>
public static class AtlasExporter
{
    /// <summary>
    /// 按指定格式生成 JSON 字符串。Generic：单文件含所有 page；
    /// PixiJS：只生成主文件（第 0 页 frames + related_multi_packs），从页 JSON 需用 ToPixiJsonPage 单独生成。
    /// </summary>
    public static string ToJson(IReadOnlyList<PackingResult> pages, IReadOnlyList<string> imageNames, AtlasFormat format = AtlasFormat.Generic)
    {
        return format == AtlasFormat.PixiJS
            ? ToPixiJson(pages, imageNames)
            : ToGenericJson(pages, imageNames);
    }

    // ============================================================
    //  通用格式（pages/regions）—— 所有非 PixiJS 引擎共用
    // ============================================================
    /// <summary>
    /// 生成通用 JSON 格式（单文件含所有 page 与每页精灵帧信息）。
    /// 结构：{ pages: [ { image, width, height, regions: [ { name, x, y, w, h, rotated, sourceW, sourceH } ] } ], animations? }
    /// 由强类型模型 <see cref="AtlasData"/> 负责序列化（走 AtlasJsonContext 源生成，避免 AOT 下的反射 IL3050/IL2026）。
    /// </summary>
    public static string ToGenericJson(IReadOnlyList<PackingResult> pages, IReadOnlyList<string> imageNames)
        => AtlasData.FromPackingResults(pages, imageNames).ToJson();

    // ============================================================
    //  PixiJS v8 Spritesheet 官方格式（frames / meta / animations）
    // ============================================================
    /// <summary>
    /// 生成 PixiJS v8 Spritesheet 官方格式（主文件，含第 0 页 frames）。
    /// 多页：主文件 meta.related_multi_packs 列出其余页的 JSON 文件名（后缀与写盘一致，
    /// 由 suffix 参数传入；配套的从页 JSON 请用 ToPixiJsonPage 写入同名文件）。
    /// animations 只包含「帧全部在第 0 页」的分组（避免引用其它页不存在的帧导致 undefined）。
    /// 注意：PixiJS 对 rotated=true 的帧会在解析时内部交换 frame.w/h，
    /// 因此 frame 必须填「源方向」尺寸（SourceWidth/SourceHeight），与 TexturePacker 官方约定一致。
    /// </summary>
    public static string ToPixiJson(IReadOnlyList<PackingResult> pages, IReadOnlyList<string> imageNames, string atlasBaseName = "atlas", string suffix = ".json")
    {
        var sheet = new PixiAtlasSheet
        {
            Meta = new PixiMetaData
            {
                Image = imageNames[0],
                Size = new PixiSizeData(pages[0].AtlasWidth, pages[0].AtlasHeight),
                Scale = 1,
            },
        };

        foreach (var p in pages[0].Sprites)
            sheet.Frames[p.Name] = BuildPixiFrame(p);

        if (pages.Count > 1)
        {
            var related = new List<string>();
            for (int i = 1; i < pages.Count; i++)
                related.Add(atlasBaseName + "_" + i + suffix);
            sheet.Meta.RelatedMultiPacks = related;
        }

        var anims = BuildAnimationsForPage(pages, 0);
        sheet.Animations = anims is { Count: > 0 } ? anims : null;
        return sheet.ToJson();
    }

    /// <summary>
    /// 多页时，生成第 i 页（i&gt;0）的独立 PixiJS JSON 文件内容（仅含本页 frames 与本页动画分组）。
    /// animations 由 BuildAnimationsForPage 提供，只含帧在本页的分组。
    /// </summary>
    public static string ToPixiJsonPage(PackingResult page, string imageName, Dictionary<string, List<string>>? animations = null)
    {
        var sheet = new PixiAtlasSheet
        {
            Meta = new PixiMetaData
            {
                Image = imageName,
                Size = new PixiSizeData(page.AtlasWidth, page.AtlasHeight),
                Scale = 1,
            },
        };

        foreach (var p in page.Sprites)
            sheet.Frames[p.Name] = BuildPixiFrame(p);

        sheet.Animations = animations is { Count: > 0 } ? animations : null;
        return sheet.ToJson();
    }

    private static PixiFrameData BuildPixiFrame(PackedSprite p)
    {
        // frame 始终填源方向尺寸（rotated 时由 PixiJS 解析器内部交换为图集内尺寸）
        return new PixiFrameData
        {
            Frame = new PixiRectData(p.X, p.Y, p.SourceWidth, p.SourceHeight),
            Rotated = p.Rotated,
            Trimmed = false,
            SpriteSourceSize = new PixiRectData(0, 0, p.SourceWidth, p.SourceHeight),
            SourceSize = new PixiSizeData(p.SourceWidth, p.SourceHeight),
        };
    }

    // ============================================================
    //  动画推导：按文件名后缀 _数字 / -数字 分组（如 walk_01, walk_02 → walk）
    // ============================================================
    /// <summary>
    /// 从所有页的精灵名推导动画分组：名字以 _数字 或 -数字 结尾的，去掉数字后缀作为动画名，
    /// 同一动画名下的帧按数字从小到大排序。返回 动画名 → 帧名数组。
    /// 注意：跨页收集的动画组可能引用多个页的帧；PixiJS 多页导出请用 BuildAnimationsForPage 按页分组。
    /// </summary>
    public static Dictionary<string, List<string>> BuildAnimations(IReadOnlyList<PackingResult> pages)
    {
        var groups = new Dictionary<string, List<(int order, string name)>>();
        foreach (var page in pages)
        {
            foreach (var p in page.Sprites)
            {
                var (anim, order) = ParseAnimName(p.Name);
                if (anim is null) continue;
                if (!groups.TryGetValue(anim, out var list))
                    groups[anim] = list = new List<(int, string)>();
                list.Add((order, p.Name));
            }
        }

        var result = new Dictionary<string, List<string>>();
        foreach (var kv in groups)
        {
            kv.Value.Sort((a, b) => a.order.CompareTo(b.order));
            result[kv.Key] = kv.Value.ConvertAll(x => x.name);
        }
        return result;
    }

    /// <summary>
    /// 只统计第 pageIndex 页内精灵的动画分组（PixiJS 每页独立 JSON 时使用，
    /// 保证 animations 引用的帧都存在于本包，不会产生 undefined）。
    /// 若某动画的帧跨页，则每页各生成一个同名的部分分组（帧数少于完整动画）。
    /// </summary>
    public static Dictionary<string, List<string>> BuildAnimationsForPage(IReadOnlyList<PackingResult> pages, int pageIndex)
    {
        var groups = new Dictionary<string, List<(int order, string name)>>();
        foreach (var p in pages[pageIndex].Sprites)
        {
            var (anim, order) = ParseAnimName(p.Name);
            if (anim is null) continue;
            if (!groups.TryGetValue(anim, out var list))
                groups[anim] = list = new List<(int, string)>();
            list.Add((order, p.Name));
        }

        var result = new Dictionary<string, List<string>>();
        foreach (var kv in groups)
        {
            kv.Value.Sort((a, b) => a.order.CompareTo(b.order));
            result[kv.Key] = kv.Value.ConvertAll(x => x.name);
        }
        return result;
    }

    private static (string? anim, int order) ParseAnimName(string name)
    {
        if (string.IsNullOrEmpty(name)) return (null, 0);
        // 匹配结尾的 _12 / -03
        int i = name.Length - 1;
        int digits = 0;
        while (i >= 0 && name[i] >= '0' && name[i] <= '9') { digits++; i--; }
        if (digits == 0) return (null, 0);
        if (i < 0 || (name[i] != '_' && name[i] != '-')) return (null, 0);
        var anim = name.Substring(0, i);
        if (string.IsNullOrEmpty(anim)) return (null, 0);
        int.TryParse(name.Substring(i + 1), out int order);
        return (anim, order);
    }

    private static JsonObject AnimationsToJson(Dictionary<string, List<string>> anims)
    {
        var obj = new JsonObject();
        foreach (var kv in anims)
        {
            var arr = new JsonArray();
            foreach (var f in kv.Value) arr.Add((JsonNode)f);
            obj[kv.Key] = arr;
        }
        return obj;
    }

    /// <summary>
    /// 由精灵名推导动画分组后，转成可直接写入描述文件的 animations 节点（供自定义导出使用）。
    /// </summary>
    public static JsonObject? BuildAnimationsJson(IReadOnlyList<PackingResult> pages, int pageIndex = 0)
    {
        var anims = BuildAnimationsForPage(pages, pageIndex);
        return anims is { Count: > 0 } ? AnimationsToJson(anims) : null;
    }

    // ============================================================
    //  libGDX .atlas 文本格式（单文件含所有 page）
    // ============================================================
    /// <summary>
    /// 生成 libGDX .atlas 文本：单文件包含全部 page，每页以图片名开头，
    /// 多页之间用空行分隔。imageNames[i] 对应 pages[i] 的图片文件名（如 atlas_0.png）。
    /// </summary>
    public static string ToAtlas(IReadOnlyList<PackingResult> pages, IReadOnlyList<string> imageNames)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < pages.Count; i++)
        {
            var result = pages[i];
            sb.AppendLine(imageNames[i]);
            sb.AppendLine("size: " + result.AtlasWidth + "," + result.AtlasHeight);
            sb.AppendLine("format: RGBA8888");
            sb.AppendLine("filter: Linear,Linear");
            sb.AppendLine("repeat: none");

            foreach (var p in result.Sprites)
            {
                sb.AppendLine(p.Name);
                sb.AppendLine("  rotate: " + (p.Rotated ? "true" : "false"));
                sb.AppendLine($"  xy: {p.X}, {p.Y}");
                sb.AppendLine($"  size: {p.Width}, {p.Height}");
                sb.AppendLine($"  orig: {p.SourceWidth}, {p.SourceHeight}");
                sb.AppendLine("  offset: 0, 0");
                sb.AppendLine("  index: -1");
            }

            if (i < pages.Count - 1)
                sb.AppendLine();
        }
        return sb.ToString();
    }
}
