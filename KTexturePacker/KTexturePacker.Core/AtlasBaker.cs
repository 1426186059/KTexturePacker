using System.Text.Json.Nodes;
using SkiaSharp;

namespace KTexturePacker.Core;

/// <summary>图集页输出的图像字节（已编码为 PNG）。</summary>
public sealed class AtlasPageOutput
{
    /// <summary>包内资源名（无扩展名），如 "atlas_0"。运行端按 GetFileNameWithoutExtension(image) 解析回此名取纹理。</summary>
    public string Name { get; set; } = "";

    public byte[] Bytes { get; set; } = Array.Empty<byte>();
    public int Width { get; set; }
    public int Height { get; set; }
}

/// <summary>待并入图集的「预切 .atlas 页」：单页 JSON（KTexturePacker 通用格式：image/width/height/regions）+ 页图像字节。</summary>
public sealed class ImportedAtlasPage
{
    public string PageJson { get; set; } = "";
    public byte[] ImageBytes { get; set; } = Array.Empty<byte>();
}

public sealed class AtlasBakeOptions
{
    /// <summary>页名前缀，如 "atlas" → 页名 atlas_0 / atlas_1 …（资源名无扩展名）。</summary>
    public string BaseName { get; set; } = "atlas";

    /// <summary>起始页索引。</summary>
    public int StartIndex { get; set; }
}

/// <summary>
/// 图集烘焙核心：把「自动装箱散图」与「导入的预切 .atlas 页」合并成同一份 AtlasData（通用格式）与每页 PNG 图像。
/// 自动散图走 MaxRects 摆放 + Skia 合成 + PNG 编码；导入页原样保留图像与子精灵坐标（只重写 image 名为本包资源名）。
/// 打包工具（KTexturePacker.Web）与内容管线（KFramework.Content.Cli）共用此核心，避免各自重复实现打包编排。
/// 纹理统一以 PNG 产出（无损中间格式），下游（引擎/GPU/平台工具链）可再按需转 ASTC、WebP 等。
/// </summary>
public static class AtlasBaker
{
    /// <summary>烘焙结果。</summary>
    public sealed class AtlasBakeResult
    {
        /// <summary>每一页的 PNG 图像（Name 为包内资源名，无扩展名）。</summary>
        public List<AtlasPageOutput> Pages { get; } = new();

        /// <summary>仅自动装箱得到的分页结果，供需要 PackingResult 的导出器（如 PixiJS）使用。</summary>
        public List<PackingResult> AutoPages { get; } = new();

        /// <summary>合并后的 KTexturePacker 通用格式 AtlasData JSON（{ pages: [...] }）。</summary>
        public string AtlasJson { get; set; } = "{\"pages\":[]}";
    }

    /// <summary>
    /// 烘焙图集。自动散图与导入页统一输出为通用格式 AtlasData JSON（pages[]）与每页 PNG 图像。
    /// 返回的 <see cref="AtlasBakeResult.Pages"/>[i].Name 作为包内纹理资源名（无扩展名），
    /// 对应 JSON 中 pages[i].image = Name + ".png"，运行端按 GetFileNameWithoutExtension(image) 取回纹理资源名。
    /// </summary>
    public static AtlasBakeResult Bake(
        List<SpriteInput>? sprites,
        List<ImportedAtlasPage>? imported,
        PackerSettings settings,
        AtlasBakeOptions? options = null)
    {
        options ??= new AtlasBakeOptions();
        var result = new AtlasBakeResult();

        int index = options.StartIndex;
        var pagesJson = new JsonArray();

        // 1) 自动装箱散图
        if (sprites is { Count: > 0 })
        {
            var packed = AtlasPacker.PackPages(sprites, settings);
            result.AutoPages.AddRange(packed);

            for (int i = 0; i < packed.Count; i++)
            {
                string name = $"{options.BaseName}_{index + i}";
                using var atlas = AtlasPacker.RenderAtlas(packed[i]);
                byte[] bytes = EncodePng(atlas);
                result.Pages.Add(new AtlasPageOutput
                {
                    Name = name,
                    Bytes = bytes,
                    Width = atlas.Width,
                    Height = atlas.Height,
                });

                // 单页通用格式 JSON（image 写成 name+.png，运行端按无扩展名取纹理）
                string pageJson = AtlasExporter.ToGenericJson(
                    new[] { packed[i] }, new[] { name + ".png" });
                var pageObj = (JsonObject)((JsonArray)((JsonObject)JsonNode.Parse(pageJson)!)["pages"]!)[0]!;
                pagesJson.Add(pageObj);
            }

            index += packed.Count;
            foreach (var s in sprites) s.Bitmap.Dispose();
        }

        // 2) 导入的预切 .atlas 页：原样保留图像与 regions，仅重写 image 名为本包资源名
        if (imported is { Count: > 0 })
        {
            foreach (var imp in imported)
            {
                string name = $"{options.BaseName}_{index}";
                index++;

                var page = (JsonObject)JsonNode.Parse(imp.PageJson)!;
                page["image"] = name + ".png";
                pagesJson.Add(page);

                int w = page["width"]?.GetValue<int>() ?? 0;
                int h = page["height"]?.GetValue<int>() ?? 0;
                result.Pages.Add(new AtlasPageOutput
                {
                    Name = name,
                    Bytes = imp.ImageBytes,
                    Width = w,
                    Height = h,
                });
            }
        }

        result.AtlasJson = new JsonObject { ["pages"] = pagesJson }.ToJsonString();
        return result;
    }

    private static byte[] EncodePng(SKBitmap bmp)
    {
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
