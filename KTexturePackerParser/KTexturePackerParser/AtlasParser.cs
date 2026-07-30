using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace KTexturePacker.Parser
{
    /// <summary>
    /// 单个子图区域（对应图集 JSON 中的 regions 项）。
    /// </summary>
    public sealed class AtlasRegion
    {
        public string Name { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public int W { get; set; } //大图集纹理中 实际占据的 像素 宽度 和 高度。
        public int H { get; set; }
        public bool Rotated { get; set; }
        public int SourceW { get; set; } //原始 宽度 和 高度
        public int SourceH { get; set; } //原始 宽度 和 高度
    }

    /// <summary>
    /// 图集的一页（对应图集 JSON 中的 pages 项）。多页图集会有多张 PNG。
    /// </summary>
    public sealed class AtlasPage
    {
        /// <summary>该页对应的 PNG 文件名，如 "AAA_0.png"。</summary>
        public string Image { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public List<AtlasRegion> Regions { get; set; } = new List<AtlasRegion>();
    }

    /// <summary>
    /// 解析后的完整图集数据。
    /// </summary>
    public sealed class AtlasData
    {
        public List<AtlasPage> Pages { get; set; } = new List<AtlasPage>();
    }

    /// <summary>
    /// 解析 KTexturePacker 生成的图集 JSON（由 AtlasExporter.ToJson 产出）。
    /// 使用 System.Text.Json 的 JsonDocument DOM 读取，无反射反序列化，
    /// 因此可在 Unity（IL2CPP/AOT）与 MonoGame 上安全运行。
    /// </summary>
    public static class AtlasParser
    {
        /// <summary>从 JSON 字符串解析图集。</summary>
        public static AtlasData Parse(string json)
        {
            if (string.IsNullOrEmpty(json))
                throw new ArgumentException("json is null or empty", nameof(json));

            var data = new AtlasData();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("pages", out var pagesEl) ||
                pagesEl.ValueKind != JsonValueKind.Array)
                return data;

            foreach (var pageEl in pagesEl.EnumerateArray())
            {
                var page = new AtlasPage
                {
                    Image = GetString(pageEl, "image"),
                    Width = GetInt(pageEl, "width"),
                    Height = GetInt(pageEl, "height"),
                };

                if (pageEl.TryGetProperty("regions", out var regionsEl) &&
                    regionsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var rEl in regionsEl.EnumerateArray())
                    {
                        page.Regions.Add(new AtlasRegion
                        {
                            Name = GetString(rEl, "name"),
                            X = GetInt(rEl, "x"),
                            Y = GetInt(rEl, "y"),
                            W = GetInt(rEl, "w"),
                            H = GetInt(rEl, "h"),
                            Rotated = GetBool(rEl, "rotated"),
                            SourceW = GetInt(rEl, "sourceW"),
                            SourceH = GetInt(rEl, "sourceH"),
                        });
                    }
                }

                data.Pages.Add(page);
            }

            return data;
        }

        /// <summary>从文件读取并解析图集 JSON。</summary>
        public static AtlasData ParseFromFile(string path)
        {
            return Parse(File.ReadAllText(path));
        }

        private static string GetString(JsonElement e, string name)
            => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;

        private static int GetInt(JsonElement e, string name)
            => e.TryGetProperty(name, out var v) && v.TryGetInt32(out int i) ? i : 0;

        private static bool GetBool(JsonElement e, string name)
            => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
    }
}
