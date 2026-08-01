using System;
using System.Collections.Generic;
using System.IO;

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
    /// 本库为**零依赖**：不引用 System.Text.Json / Newtonsoft，使用内置的轻量 JSON 读取器
    /// （见 JsonReader.cs），因此产出的 DLL 不含任何外部引用，可直接放入 Unity（Assets/.../*.dll）或 MonoGame 使用。
    /// </summary>
    public static class AtlasParser
    {
        /// <summary>从 JSON 字符串解析图集。</summary>
        public static AtlasData Parse(string json)
        {
            if (string.IsNullOrEmpty(json))
                throw new ArgumentException("json is null or empty", nameof(json));

            var data = new AtlasData();
            var reader = new JsonReader(json);
            if (!reader.ReadObject(out var root)) return data;
            if (!root.TryGetValue("pages", out var pagesVal) || pagesVal.Kind != JsonValue.KindType.Array)
                return data;

            foreach (var pageVal in pagesVal.Items)
            {
                var page = new AtlasPage
                {
                    Image = pageVal.GetString("image"),
                    Width = pageVal.GetInt("width"),
                    Height = pageVal.GetInt("height"),
                };

                if (pageVal.TryGetValue("regions", out var regionsVal) && regionsVal.Kind == JsonValue.KindType.Array)
                {
                    foreach (var rVal in regionsVal.Items)
                    {
                        page.Regions.Add(new AtlasRegion
                        {
                            Name = rVal.GetString("name"),
                            X = rVal.GetInt("x"),
                            Y = rVal.GetInt("y"),
                            W = rVal.GetInt("w"),
                            H = rVal.GetInt("h"),
                            Rotated = rVal.GetBool("rotated"),
                            SourceW = rVal.GetInt("sourceW"),
                            SourceH = rVal.GetInt("sourceH"),
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
    }
}
