using System.Text.Json.Serialization;

namespace KTexturePacker.Core.JsonFormat;

/// <summary>
/// PixiJS 尺寸 <c>{ w, h }</c>。
/// </summary>
public sealed class PixiSizeData
{
    [JsonPropertyName("w")] public int W { get; set; }
    [JsonPropertyName("h")] public int H { get; set; }

    public PixiSizeData() { }
    public PixiSizeData(int w, int h) { W = w; H = h; }
}
