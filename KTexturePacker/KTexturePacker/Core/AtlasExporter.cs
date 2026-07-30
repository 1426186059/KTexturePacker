using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace KTexturePacker.Core;

/// <summary>
/// 描述文件导出格式。
/// </summary>
public enum ExportFormat
{
    Json,
    LibGdx,
}

/// <summary>
/// 把打包结果导出为描述文件（JSON 通用格式 / libGDX .atlas 格式）。
/// </summary>
public static class AtlasExporter
{
    /// <summary>
    /// 通用 JSON 格式（兼容 TexturePacker 风格的 frames/meta 结构）。
    /// </summary>
    public static string ToJson(PackingResult result, string imageName)
    {
        var meta = new Dictionary<string, object>
        {
            ["app"] = "KTexturePacker",
            ["version"] = "1.0",
            ["image"] = imageName,
            ["format"] = "RGBA8888",
            ["size"] = new Dictionary<string, int> { ["w"] = result.AtlasWidth, ["h"] = result.AtlasHeight },
            ["scale"] = 1,
        };

        var frames = new List<Dictionary<string, object>>();
        foreach (var p in result.Sprites)
        {
            frames.Add(new Dictionary<string, object>
            {
                ["filename"] = p.Name,
                ["rotated"] = p.Rotated,
                ["trimmed"] = false,
                ["frame"] = new Dictionary<string, int> { ["x"] = p.X, ["y"] = p.Y, ["w"] = p.Width, ["h"] = p.Height },
                ["spriteSourceSize"] = new Dictionary<string, int> { ["x"] = 0, ["y"] = 0, ["w"] = p.SourceWidth, ["h"] = p.SourceHeight },
                ["sourceSize"] = new Dictionary<string, int> { ["w"] = p.SourceWidth, ["h"] = p.SourceHeight },
                ["pivot"] = new Dictionary<string, double> { ["x"] = 0.5, ["y"] = 0.5 },
            });
        }

        var root = new Dictionary<string, object>
        {
            ["meta"] = meta,
            ["frames"] = frames,
        };

        return JsonSerializer.Serialize(root, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// libGDX .atlas 文本格式。
    /// </summary>
    public static string ToLibGdx(PackingResult result, string imageName)
    {
        var sb = new StringBuilder();
        sb.AppendLine(imageName);
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

        return sb.ToString();
    }
}
