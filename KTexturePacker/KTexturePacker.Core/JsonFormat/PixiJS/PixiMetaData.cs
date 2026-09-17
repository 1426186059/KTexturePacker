using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace KTexturePacker.Core.JsonFormat;

/// <summary>
/// PixiJS meta：<c>{ image, size, scale, related_multi_packs? }</c>。
/// </summary>
public sealed class PixiMetaData
{
    [JsonPropertyName("image")] public string Image { get; set; } = "";

    [JsonPropertyName("size")] public PixiSizeData? Size { get; set; }

    [JsonPropertyName("scale")] public double Scale { get; set; } = 1;

    /// <summary>多图集时，其余各页 JSON 的文件名列表；为 null 时不写出。</summary>
    [JsonPropertyName("related_multi_packs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? RelatedMultiPacks { get; set; }
}
