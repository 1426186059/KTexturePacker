using System.Text.Json.Serialization;

namespace KTexturePacker.Core.JsonFormat;

/// <summary>
/// PixiJS 单帧：<c>{ frame, rotated, trimmed, spriteSourceSize, sourceSize }</c>。
/// 注：frame.w/h 按 PixiJS 官方约定填<b>源方向</b>尺寸（解析器内部按 rotated 交换）。
/// </summary>
public sealed class PixiFrameData
{
    [JsonPropertyName("frame")] public PixiRectData? Frame { get; set; }
    [JsonPropertyName("rotated")] public bool Rotated { get; set; }
    [JsonPropertyName("trimmed")] public bool Trimmed { get; set; }
    [JsonPropertyName("spriteSourceSize")] public PixiRectData? SpriteSourceSize { get; set; }
    [JsonPropertyName("sourceSize")] public PixiSizeData? SourceSize { get; set; }
}
