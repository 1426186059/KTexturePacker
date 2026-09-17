using System.Text.Json.Serialization;

namespace KTexturePacker.Core.JsonFormat;

/// <summary>
/// 通用格式的一个子图区域：<c>{ name, x, y, w, h, rotated, sourceW, sourceH }</c>。
/// </summary>
public sealed class AtlasRegionData
{
    /// <summary>子图名（不含扩展名），反解后即输出文件名。</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>占位矩形左上角在图集中的像素坐标（原点左上，X 向右、Y 向下）。</summary>
    [JsonPropertyName("x")] public int X { get; set; }
    [JsonPropertyName("y")] public int Y { get; set; }

    /// <summary>
    /// 占位矩形在图集中实际占据的尺寸。<br/>
    /// 未旋转 = 源尺寸；旋转 90° 时（<see cref="Rotated"/>）源宽高互换：<c>W = SourceH, H = SourceW</c>。
    /// </summary>
    [JsonPropertyName("w")] public int W { get; set; }
    [JsonPropertyName("h")] public int H { get; set; }

    /// <summary>是否被顺时针旋转 90° 存放（反解时按逆时针 90° 还原）。</summary>
    [JsonPropertyName("rotated")]
    public bool Rotated { get; set; }

    /// <summary>子图原始宽度（px）。</summary>
    [JsonPropertyName("sourceW")]
    public int SourceW { get; set; }

    /// <summary>子图原始高度（px）。</summary>
    [JsonPropertyName("sourceH")]
    public int SourceH { get; set; }

    /// <summary>从一次打包结果中的精灵信息构建。</summary>
    public static AtlasRegionData FromPackedSprite(PackedSprite s) => new()
    {
        Name = s.Name,
        X = s.X,
        Y = s.Y,
        W = s.Width,
        H = s.Height,
        Rotated = s.Rotated,
        SourceW = s.SourceWidth,
        SourceH = s.SourceHeight,
    };

    /// <summary>还原旋转后应得到的正向宽度；<see cref="SourceW"/> 缺失（0）时按占位矩形推算。</summary>
    [JsonIgnore]
    public int RestoredWidth => SourceW > 0 ? SourceW : (Rotated ? H : W);

    /// <summary>还原旋转后应得到的正向高度；<see cref="SourceH"/> 缺失（0）时按占位矩形推算。</summary>
    [JsonIgnore]
    public int RestoredHeight => SourceH > 0 ? SourceH : (Rotated ? W : H);
}
