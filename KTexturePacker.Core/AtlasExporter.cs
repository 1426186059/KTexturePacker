using System.Collections.Generic;
using System.Text;

namespace KTexturePacker.Core;

/// <summary>
/// 把打包结果导出为描述文件（libGDX .atlas 文本格式，单文件含所有 page）。
/// </summary>
public static class AtlasExporter
{
    /// <summary>
    /// 生成 libGDX .atlas 文本：单文件包含全部 page，每页以图片名开头，
    /// 多页之间用空行分隔。imageNames[i] 对应 pages[i] 的图片文件名（如 atlas_0.png）。
    /// </summary>
    public static string ToAtlas(IReadOnlyList<PackingResult> pages, IReadOnlyList<string> imageNames)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < pages.Count; i++)
        {
            var result = pages[i];
            sb.AppendLine(imageNames[i]);
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

            if (i < pages.Count - 1)
                sb.AppendLine();
        }
        return sb.ToString();
    }
}
