using System.Text.Json.Serialization;

namespace KTexturePacker.Core.JsonFormat;

/// <summary>
/// PixiJS 区域矩形 <c>{ x, y, w, h }</c>。原点在图集左上角（像素空间）。
/// </summary>
public sealed class PixiRectData
{
    [JsonPropertyName("x")] public int X { get; set; }
    [JsonPropertyName("y")] public int Y { get; set; }
    [JsonPropertyName("w")] public int W { get; set; }
    [JsonPropertyName("h")] public int H { get; set; }

    public PixiRectData() { }
    public PixiRectData(int x, int y, int w, int h) { X = x; Y = y; W = w; H = h; }
}
