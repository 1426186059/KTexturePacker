using System.Text.Json.Nodes;
using SkiaSharp;

namespace KTexturePacker.Core;

/// <summary>
/// 图集单页的「源像素」（RGBA8，行优先，长度 = Width * Height * 4）。
/// 这是 KTexturePacker 产出的<b>通用中间格式</b>——它不绑定任何图像编码，
/// 下游（内容管线 / 平台工具链）可据此自由转成 PNG、WebP、ASTC、KTX 等任意目标格式。
/// KTexturePacker 自身则通过 <see cref="PngEncoder"/> 把这份额色数据编码为 PNG，作为默认交付物。
/// </summary>
public sealed class AtlasPageOutput
{
    /// <summary>包内资源名（无扩展名），如 "atlas_0"。运行端按 GetFileNameWithoutExtension(image) 解析回此名取纹理。</summary>
    public string Name { get; set; } = "";

    /// <summary>源像素 RGBA8（行优先，未编码）。长度应为 Width * Height * 4。</summary>
    public byte[] RgbaPixels { get; set; } = Array.Empty<byte>();

    public int Width { get; set; }
    public int Height { get; set; }

    /// <summary>便捷方法：把本页 RGBA 源像素编码为 PNG 字节（KTexturePacker 默认交付格式）。</summary>
    public byte[] ToPng() => PngEncoder.Encode(RgbaPixels, Width, Height);
}

/// <summary>待并入图集的「预切 .atlas 页」：单页 JSON（KTexturePacker 通用格式）+ 页图像字节（PNG）。</summary>
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
/// 图集烘焙核心：把「自动装箱散图」与「导入的预切 .atlas 页」合并成同一份 AtlasData（通用格式）与每页源像素（RGBA8）。
/// <para>
/// 核心<b>只产出 RGBA8 中间格式</b>；PNG 是 KTexturePacker 自己用 <see cref="PngEncoder"/> 从该 RGBA 编码出的默认交付物，
/// 其它格式（WebP / ASTC / KTX 等）由下游任取 RGBA 自行转换——核心不关心最终编码。
/// 这样既保证上游与下游共用同一份排版结果，又让下游拥有完全的格式自由度。
/// </para>
/// 打包工具（KTexturePacker.Web）与内容管线（KFramework.Content.Cli）共用此核心，避免各自重复实现打包编排。
/// </summary>
public static class AtlasBaker
{
    /// <summary>烘焙结果。</summary>
    public sealed class AtlasBakeResult
    {
        /// <summary>每一页的源像素（RGBA8）。Name 为包内纹理资源名（无扩展名）。</summary>
        public List<AtlasPageOutput> Pages { get; } = new();

        /// <summary>仅自动装箱得到的分页结果，供需要 PackingResult 的导出器（如 PixiJS）使用。</summary>
        public List<PackingResult> AutoPages { get; } = new();

        /// <summary>合并后的 KTexturePacker 通用格式 AtlasData JSON（{ pages: [...] }）。</summary>
        public string AtlasJson { get; set; } = "{\"pages\":[]}";
    }

    /// <summary>
    /// 烘焙图集。自动散图与导入页统一输出为通用格式 AtlasData JSON（pages[]）与每页 RGBA8 源像素。
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

        // 1) 自动装箱散图：MaxRects 摆放 + Skia 合成 → 取 RGBA8 源像素
        if (sprites is { Count: > 0 })
        {
            var packed = AtlasPacker.PackPages(sprites, settings);
            result.AutoPages.AddRange(packed);

            for (int i = 0; i < packed.Count; i++)
            {
                string name = $"{options.BaseName}_{index + i}";
                using var atlas = AtlasPacker.RenderAtlas(packed[i]);
                byte[] rgba = GetRgba(atlas);

                result.Pages.Add(new AtlasPageOutput
                {
                    Name = name,
                    RgbaPixels = rgba,
                    Width = atlas.Width,
                    Height = atlas.Height,
                });

                // 单页通用格式 JSON（image 写成 name+.png，运行端按无扩展名取纹理）
                string pageJson = AtlasExporter.ToGenericJson(
                    new[] { packed[i] }, new[] { name + ".png" });
                var pageObj = (JsonObject)((JsonArray)((JsonObject)JsonNode.Parse(pageJson)!)["pages"]!)[0]!;
                // DeepClone 拿到脱离父节点的副本：pageObj 仍挂在 JsonNode.Parse 的临时树上，
                // 直接加入 pagesJson 会抛 "The node already has a parent"。
                pagesJson.Add(pageObj.DeepClone());
            }

            index += packed.Count;
            foreach (var s in sprites) s.Bitmap.Dispose();
        }

        // 2) 导入的预切 .atlas 页：把 PNG 字节解码为 RGBA8 源像素（统一为中间格式），
        //    原样保留 regions，仅重写 image 名为本包资源名。
        if (imported is { Count: > 0 })
        {
            foreach (var imp in imported)
            {
                string name = $"{options.BaseName}_{index}";
                index++;

                var page = (JsonObject)JsonNode.Parse(imp.PageJson)!;
                page["image"] = name + ".png";
                pagesJson.Add(page);

                using var bmp = SKBitmap.Decode(imp.ImageBytes)
                    ?? throw new InvalidDataException("导入的 .atlas 页图像解码失败。");
                byte[] rgba = GetRgba(bmp);

                result.Pages.Add(new AtlasPageOutput
                {
                    Name = name,
                    RgbaPixels = rgba,
                    Width = bmp.Width,
                    Height = bmp.Height,
                });
            }
        }

        result.AtlasJson = new JsonObject { ["pages"] = pagesJson }.ToJsonString();
        return result;
    }

    /// <summary>
    /// 从任意 SKBitmap 取出 <b>RGBA8（非预乘）</b>源像素，作为统一中间格式。
    /// 不论上游位图是何色彩类型 / 是否预乘，均归一化为 RGBA8888 Unpremul，
    /// 保证下游拿到的是一份确定格式的像素，可自由转码为任意目标格式。
    /// </summary>
    private static byte[] GetRgba(SKBitmap src)
    {
        int w = src.Width, h = src.Height;
        var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        byte[] rgba = new byte[w * h * 4];
        // 此版本 ReadPixels 的像素缓冲接收 IntPtr，故固定数组后传入。
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(rgba, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            using var img = SKImage.FromBitmap(src);
            img.ReadPixels(info, handle.AddrOfPinnedObject(), w * 4, 0, 0);
        }
        finally
        {
            handle.Free();
        }
        return rgba;
    }
}

/// <summary>
/// 把 RGBA8 源像素编码为 PNG 字节。KTexturePacker 自身用它把核心产出的 RGBA 中间格式落地为 PNG 默认交付物；
/// 下游如需其它格式，可改用各自的编码器（如 WebP）直接消费 RGBA，无需经由 PNG。
/// </summary>
public static class PngEncoder
{
    public static byte[] Encode(byte[] rgba, int width, int height)
    {
        using var bmp = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        rgba.CopyTo(bmp.GetPixelSpan());
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
