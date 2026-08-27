using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace KTexturePacker.Core;

/// <summary>
/// 描述文件（JSON）导出格式。只有 PixiJS 需要专属的 Spritesheet 官方结构；
/// 其余引擎（Unity / UE5 / MonoGame / 通用）共用 KTexturePacker 通用格式，供 AtlasParser 解析。
/// </summary>
public enum AtlasFormat
{
    /// <summary>KTexturePacker 通用格式（pages/regions），后缀 .atlas.txt。Unity / UE5 / MonoGame 端解析器均消费此格式。</summary>
    Generic,

    /// <summary>PixiJS v8 Spritesheet 官方格式（frames/meta/animations），后缀 .pixi.json。PixiJS 的 Assets.load 可直接识别，无需自定义 loader。</summary>
    PixiJS,
}

/// <summary>
/// 把打包结果导出为描述文件（libGDX .atlas 文本格式 / 各引擎 JSON 格式）。
/// </summary>
public static class AtlasExporter
{
    /// <summary>
    /// 按指定格式生成 JSON 字符串（单文件含所有 page）。
    /// 仅 PixiJS 输出其专属官方结构；其余格式统一输出通用 pages/regions 结构。
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
    /// 使用 JsonNode 构建，避免 AOT 下的反射（IL3050/IL2026）。
    /// </summary>
    public static string ToGenericJson(IReadOnlyList<PackingResult> pages, IReadOnlyList<string> imageNames)
    {
        var pagesArr = new JsonArray();
        for (int i = 0; i < pages.Count; i++)
        {
            var result = pages[i];
            var pageObj = new JsonObject
            {
                ["image"] = imageNames[i],
                ["width"] = result.AtlasWidth,
                ["height"] = result.AtlasHeight,
            };
            var regions = new JsonArray();
            foreach (var p in result.Sprites)
            {
                var region = new JsonObject
                {
                    ["name"] = p.Name,
                    ["x"] = p.X,
                    ["y"] = p.Y,
                    ["w"] = p.Width,
                    ["h"] = p.Height,
                    ["rotated"] = p.Rotated,
                    ["sourceW"] = p.SourceWidth,
                    ["sourceH"] = p.SourceHeight,
                };
                regions.Add((JsonNode)region);
            }
            pageObj["regions"] = regions;
            pagesArr.Add((JsonNode)pageObj);
        }
        var root = new JsonObject { ["pages"] = pagesArr };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    }

    // ============================================================
    //  PixiJS v8 Spritesheet 官方格式（frames / meta / animations）
    // ============================================================
    /// <summary>
    /// 生成 PixiJS v8 Spritesheet 官方格式（单文件含第 0 页 frames）。
    /// 多页：主文件含第 0 页 frames，meta.related_multi_packs 列出其余页 JSON（可由 ToPixiJsonPage 写入同名文件）。
    /// 注意：PixiJS 对 rotated=true 的帧会在解析时内部交换 frame.w/h，
    /// 因此 frame 必须填「源方向」尺寸（SourceWidth/SourceHeight），与 TexturePacker 官方约定一致。
    /// </summary>
    public static string ToPixiJson(IReadOnlyList<PackingResult> pages, IReadOnlyList<string> imageNames, string atlasBaseName = "atlas")
    {
        var frames = new JsonObject();
        for (int i = 0; i < pages[0].Sprites.Count; i++)
        {
            var p = pages[0].Sprites[i];
            frames[p.Name] = BuildPixiFrame(p);
        }

        var meta = new JsonObject
        {
            ["image"] = imageNames[0],
            ["size"] = new JsonObject { ["w"] = pages[0].AtlasWidth, ["h"] = pages[0].AtlasHeight },
            ["scale"] = 1,
        };

        if (pages.Count > 1)
        {
            var related = new JsonArray();
            for (int i = 1; i < pages.Count; i++)
                related.Add((JsonNode)(atlasBaseName + "_" + i + ".json"));
            meta["related_multi_packs"] = related;
        }

        var root = new JsonObject
        {
            ["frames"] = frames,
            ["meta"] = meta,
        };
        var anims = BuildAnimations(pages);
        if (anims.Count > 0) root["animations"] = AnimationsToJson(anims);
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    }

    /// <summary>多页时，生成第 i 页（i>0）的独立 PixiJS JSON 文件内容（仅含本页 frames）。</summary>
    public static string ToPixiJsonPage(PackingResult page, string imageName)
    {
        var frames = new JsonObject();
        foreach (var p in page.Sprites)
            frames[p.Name] = BuildPixiFrame(p);

        var root = new JsonObject
        {
            ["frames"] = frames,
            ["meta"] = new JsonObject
            {
                ["image"] = imageName,
                ["size"] = new JsonObject { ["w"] = page.AtlasWidth, ["h"] = page.AtlasHeight },
                ["scale"] = 1,
            },
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    }

    private static JsonObject BuildPixiFrame(PackedSprite p)
    {
        // frame 始终填源方向尺寸（rotated 时由 PixiJS 解析器内部交换为图集内尺寸）
        var f = new JsonObject
        {
            ["frame"] = new JsonObject { ["x"] = p.X, ["y"] = p.Y, ["w"] = p.SourceWidth, ["h"] = p.SourceHeight },
            ["rotated"] = p.Rotated,
            ["trimmed"] = false,
            ["spriteSourceSize"] = new JsonObject { ["x"] = 0, ["y"] = 0, ["w"] = p.SourceWidth, ["h"] = p.SourceHeight },
            ["sourceSize"] = new JsonObject { ["w"] = p.SourceWidth, ["h"] = p.SourceHeight },
        };
        return f;
    }

    // ============================================================
    //  动画推导：按文件名后缀 _数字 / -数字 分组（如 walk_01, walk_02 → walk）
    // ============================================================
    /// <summary>
    /// 从所有页的精灵名推导动画分组：名字以 _数字 或 -数字 结尾的，去掉数字后缀作为动画名，
    /// 同一动画名下的帧按数字从小到大排序。返回 动画名 → 帧名数组。
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
