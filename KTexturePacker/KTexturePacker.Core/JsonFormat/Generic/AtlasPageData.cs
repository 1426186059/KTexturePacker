using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace KTexturePacker.Core.JsonFormat;

/// <summary>
/// 通用格式的一页：<c>{ image, width, height, regions: [...] }</c>。
/// </summary>
public sealed class AtlasPageData
{
    /// <summary>该页图集图片文件名，如 "atlas_0.png"。</summary>
    [JsonPropertyName("image")]
    public string Image { get; set; } = "";

    /// <summary>整页图集宽度（px）。</summary>
    [JsonPropertyName("width")]
    public int Width { get; set; }

    /// <summary>整页图集高度（px）。</summary>
    [JsonPropertyName("height")]
    public int Height { get; set; }

    /// <summary>本页包含的所有子图区域。</summary>
    [JsonPropertyName("regions")]
    public List<AtlasRegionData> Regions { get; set; } = new();

    /// <summary>从一次打包结果构建本页描述。</summary>
    public static AtlasPageData FromPackingResult(PackingResult page, string imageName)
    {
        var pageData = new AtlasPageData
        {
            Image = imageName,
            Width = page.AtlasWidth,
            Height = page.AtlasHeight,
        };
        foreach (var s in page.Sprites)
            pageData.Regions.Add(AtlasRegionData.FromPackedSprite(s));
        return pageData;
    }
}
