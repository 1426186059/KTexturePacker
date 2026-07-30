using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

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
    /// 使用 JsonNode 构建，避免反射式序列化，可在原生 AOT 下安全运行。
    /// </summary>
    public static string ToJson(PackingResult result, string imageName)
    {
        var meta = new JsonObject
        {
            ["app"] = "KTexturePacker",
            ["version"] = "1.0",
            ["image"] = imageName,
            ["format"] = "RGBA8888",
            ["size"] = new JsonObject { ["w"] = result.AtlasWidth, ["h"] = result.AtlasHeight },
            ["scale"] = 1,
        };

        var frames = new JsonArray();
        foreach (var p in result.Sprites)
        {
            frames.Add((JsonNode)new JsonObject
            {
                ["filename"] = p.Name,
                ["rotated"] = p.Rotated,
                ["trimmed"] = false,
                ["frame"] = new JsonObject { ["x"] = p.X, ["y"] = p.Y, ["w"] = p.Width, ["h"] = p.Height },
                ["spriteSourceSize"] = new JsonObject { ["x"] = 0, ["y"] = 0, ["w"] = p.SourceWidth, ["h"] = p.SourceHeight },
                ["sourceSize"] = new JsonObject { ["w"] = p.SourceWidth, ["h"] = p.SourceHeight },
                ["pivot"] = new JsonObject { ["x"] = 0.5, ["y"] = 0.5 },
            });
        }

        var root = new JsonObject
        {
            ["meta"] = meta,
            ["frames"] = frames,
        };

        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
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
