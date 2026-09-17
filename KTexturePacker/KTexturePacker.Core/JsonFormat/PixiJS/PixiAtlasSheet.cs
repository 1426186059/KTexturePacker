using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KTexturePacker.Core.JsonFormat;

// ============================================================================
//  JSON 格式 #2：PixiJS v8 Spritesheet 官方格式（frames / meta / animations）
//  PixiJS 的 Assets.load 可直接识别，无需自定义 loader。
// ============================================================================

/// <summary>
/// PixiJS v8 Spritesheet 描述根对象：<c>{ frames, meta, animations? }</c>。
/// </summary>
public sealed class PixiAtlasSheet
{
    /// <summary>帧名 → 帧数据。</summary>
    [JsonPropertyName("frames")]
    public Dictionary<string, PixiFrameData> Frames { get; set; } = new();

    [JsonPropertyName("meta")]
    public PixiMetaData Meta { get; set; } = new();

    /// <summary>动画分组（动画名 → 帧名列表）；为 null 时不写出。</summary>
    [JsonPropertyName("animations")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, List<string>>? Animations { get; set; }

    // ---------- 读 / 写 ----------
    public static PixiAtlasSheet FromJson(string json) =>
        JsonSerializer.Deserialize(json, AtlasJsonContext.Default.PixiAtlasSheet) ?? new PixiAtlasSheet();

    public static PixiAtlasSheet FromFile(string path) => FromJson(File.ReadAllText(path));

    public string ToJson() => JsonSerializer.Serialize(this, AtlasJsonContext.Default.PixiAtlasSheet);

    /// <summary>
    /// 转成通用格式模型（只产出 meta 对应的这一页）。
    /// PixiJS 的 frame / sourceSize 填的都是「源方向」尺寸，rotated 表示图集内是否旋转存放，
    /// 因此图集内占位矩形尺寸需在 rotated 时互换 w/h。
    /// </summary>
    public AtlasData ToAtlasData()
    {
        var page = new AtlasPageData
        {
            Image = Meta.Image,
            Width = Meta.Size?.W ?? 0,
            Height = Meta.Size?.H ?? 0,
        };

        foreach (var kv in Frames)
        {
            var f = kv.Value;
            int sw = f.SourceSize?.W ?? f.Frame?.W ?? 0;
            int sh = f.SourceSize?.H ?? f.Frame?.H ?? 0;
            int x = f.Frame?.X ?? 0;
            int y = f.Frame?.Y ?? 0;
            page.Regions.Add(new AtlasRegionData
            {
                Name = kv.Key,
                X = x,
                Y = y,
                // 图集内占位矩形：未旋转 = 源尺寸；旋转 90° = 源宽高互换
                W = f.Rotated ? sh : sw,
                H = f.Rotated ? sw : sh,
                Rotated = f.Rotated,
                SourceW = sw,
                SourceH = sh,
            });
        }

        return new AtlasData { Pages = { page }, Animations = Animations is { Count: > 0 } ? Animations : null };
    }
}
