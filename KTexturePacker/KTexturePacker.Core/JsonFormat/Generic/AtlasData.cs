using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KTexturePacker.Core.JsonFormat;

// ============================================================================
//  JSON 格式 #1：KTexturePacker 通用格式（pages / regions）
//  Unity / UE5 / MonoGame / 通用端的解析器（KTexturePacker.Parser）均消费此结构。
// ============================================================================

/// <summary>
/// 通用格式根对象：<c>{ pages: [ { image, width, height, regions: [...] } ], animations? }</c>。
/// </summary>
public sealed class AtlasData
{
    /// <summary>所有图集页（每页对应一张图片）。</summary>
    [JsonPropertyName("pages")]
    public List<AtlasPageData> Pages { get; set; } = new();

    /// <summary>
    /// 可选动画分组（动画名 → 帧名列表）。为 null 时不写入 JSON，
    /// 保证默认输出只有 pages 一个字段（与历史产物一致）。
    /// </summary>
    [JsonPropertyName("animations")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, List<string>>? Animations { get; set; }

    // ---------- 读 ----------
    /// <summary>从 JSON 字符串反序列化（走 <see cref="AtlasJsonContext"/> 源生成，AOT 安全）。</summary>
    public static AtlasData FromJson(string json) =>
        JsonSerializer.Deserialize(json, AtlasJsonContext.Default.AtlasData) ?? new AtlasData();

    /// <summary>从描述文件读取并反序列化。</summary>
    public static AtlasData FromFile(string path) => FromJson(File.ReadAllText(path));

    // ---------- 写 ----------
    /// <summary>序列化为紧凑 JSON（字段顺序/命名与 AtlasExporter 历史输出一致）。</summary>
    public string ToJson() => JsonSerializer.Serialize(this, AtlasJsonContext.Default.AtlasData);

    // ---------- 格式互转 ----------
    /// <summary>把 PixiJS v8 描述转换为通用格式模型（frames / meta → pages / regions）。</summary>
    public static AtlasData FromPixiSheet(PixiAtlasSheet sheet) => sheet.ToAtlasData();

    // ---------- 由打包结果构建（导出侧） ----------
    /// <summary>按「页 → image 名」一一对应，从打包结果构建通用格式模型。</summary>
    public static AtlasData FromPackingResults(IReadOnlyList<PackingResult> pages, IReadOnlyList<string> imageNames)
    {
        var data = new AtlasData();
        for (int i = 0; i < pages.Count; i++)
            data.Pages.Add(AtlasPageData.FromPackingResult(pages[i], i < imageNames.Count ? imageNames[i] : ""));
        return data;
    }

    /// <summary>挂上动画分组；为空则不写入 JSON。</summary>
    public AtlasData WithAnimations(Dictionary<string, List<string>>? animations)
    {
        Animations = animations is { Count: > 0 } ? animations : null;
        return this;
    }
}
