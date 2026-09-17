using System.Text.Json;
using KTexturePacker.Core.JsonFormat;
using SkiaSharp;

namespace KTexturePacker.Core;

/// <summary>
/// 反解（拆图）得到的单张小图。
/// </summary>
public sealed class UnpackedSprite
{
    /// <summary>子图名（来自描述文件的 name / frames 键名，不含扩展名）。</summary>
    public string Name { get; init; } = "";

    /// <summary>实际写入磁盘的文件名（已清洗非法字符并保证唯一）。</summary>
    public string FileName { get; init; } = "";

    /// <summary>还原后（正向）宽度，px。</summary>
    public int Width { get; init; }

    /// <summary>还原后（正向）高度，px。</summary>
    public int Height { get; init; }

    /// <summary>原图在图集中是否以顺时针 90° 存放（本类输出已是还原后的正向图）。</summary>
    public bool WasRotated { get; init; }

    /// <summary>图集中的原始占位矩形位置。</summary>
    public int SourceX { get; init; }
    public int SourceY { get; init; }

    /// <summary>编码后的图片字节（默认 PNG）。</summary>
    public byte[] Bytes { get; init; } = Array.Empty<byte>();
}

/// <summary>
/// 一次反解的结果（只处理一张图集 = 描述文件中的一页）。
/// </summary>
public sealed class AtlasUnpackResult
{
    /// <summary>被反解的描述文件（如 atlas.atlas.txt）。</summary>
    public string DescriptionFile { get; set; } = "";

    /// <summary>被反解的图集图片文件（如 atlas_0.png）。</summary>
    public string AtlasImage { get; set; } = "";

    /// <summary>该页声明的尺寸（px）。</summary>
    public int PageWidth { get; set; }
    public int PageHeight { get; set; }

    /// <summary>是否来自 PixiJS 格式的转换。</summary>
    public bool FromPixiJs { get; set; }

    /// <summary>批次反解时本图集的标识名（通常取描述文件名），用作输出子目录名。</summary>
    public string Key { get; set; } = "";

    /// <summary>批次反解时本图集小图的实际输出目录（<see cref="AtlasFolderUnpacker"/> 写入后置值）。</summary>
    public string OutputFolder { get; set; } = "";

    /// <summary>拆出来的小图。</summary>
    public List<UnpackedSprite> Sprites { get; } = new();

    /// <summary>因区域越界/尺寸非法而被跳过的数量。</summary>
    public int Skipped { get; set; }
}

/// <summary>
/// 图集反解器：把「已发布的图集（PNG/WebP… + 描述文件）」拆回一张张小图。
/// <para>
/// 支持两种描述文件：<br/>
/// · KTexturePacker 通用格式（pages/regions）；<br/>
/// · PixiJS v8 Spritesheet（frames/meta），内部先转成统一的 <see cref="AtlasData"/> 模型。<br/>
/// </para>
/// 旋转约定：打包时（<see cref="AtlasPacker.RenderAtlas"/>）用 Skia 顺时针 90° 存放旋转子图，
/// 反解时对 <c>rotated=true</c> 的区域做逆时针 90° 还原，得到与原始素材同向的图片。
/// </summary>
/// <remarks>按文件夹批量反解请用 <see cref="AtlasFolderUnpacker"/>。</remarks>
public static class AtlasUnpacker
{
    /// <summary>反解输出的图片编码格式。</summary>
    public enum OutputFormat
    {
        Png,
        Webp,
    }

    private const int MaxSpriteDimension = 16384;

    // ==================================================================
    //  1) 读取描述文件 → 统一强类型模型
    // ==================================================================

    /// <summary>
    /// 自动识别描述文件格式并反序列化为统一的通用格式模型 <see cref="AtlasData"/>。
    /// </summary>
    /// <exception cref="InvalidDataException">既不是通用格式也不是 PixiJS 格式。</exception>
    public static AtlasData LoadDescription(string json, out bool isPixiJs)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new InvalidDataException("描述文件内容为空。");

        bool pixi;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("描述文件根节点不是 JSON 对象。");
            pixi = root.TryGetProperty("frames", out _) && root.TryGetProperty("meta", out _);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("描述文件不是合法 JSON：" + ex.Message, ex);
        }

        isPixiJs = pixi;
        try
        {
            return pixi
                ? PixiAtlasSheet.FromJson(json).ToAtlasData()
                : AtlasData.FromJson(json);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("描述文件结构与期待不符：" + ex.Message, ex);
        }
    }

    /// <summary>读取描述文件并转为统一模型。</summary>
    public static AtlasData LoadDescriptionFile(string path) => LoadDescription(File.ReadAllText(path), out _);

    /// <summary>
    /// 只给定图集图片时，在同级目录下猜测它的描述文件：
    /// 逐个尝试 *.atlas.txt / *.atlas.json / *.json，取第一个「页 image 名」能匹配该图集文件的。
    /// </summary>
    public static string? TryFindDescriptionFile(string atlasImagePath, string? searchFolder = null)
    {
        if (string.IsNullOrWhiteSpace(atlasImagePath)) return null;
        string folder = searchFolder ?? Path.GetDirectoryName(Path.GetFullPath(atlasImagePath)) ?? "";
        if (!Directory.Exists(folder)) return null;

        string target = Path.GetFileName(atlasImagePath);
        string targetStem = Path.GetFileNameWithoutExtension(atlasImagePath);

        var candidates = Directory.EnumerateFiles(folder)
            .Where(f =>
            {
                var ext = Path.GetExtension(f).ToLowerInvariant();
                return ext is ".json" or ".txt" or ".atlas" || f.EndsWith(".atlas.txt", StringComparison.OrdinalIgnoreCase);
            })
            // 与图片同名的优先
            .OrderBy(f => Path.GetFileNameWithoutExtension(f).StartsWith(targetStem, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(f => f, StringComparer.OrdinalIgnoreCase);

        foreach (var f in candidates)
        {
            try
            {
                if (ResolvePageOrNull(LoadDescriptionFile(f), target) is not null) return f;
            }
            catch
            {
                // 不是图集描述文件，继续尝试下一个
            }
        }
        return null;
    }

    // ==================================================================
    //  2) 选定要反解的那一页
    // ==================================================================

    /// <summary>
    /// 从描述文件中挑出「要反解的那一页」。
    /// 优先按图集文件名匹配 pages[].image；无匹配时，若只有一页则用它，多页则报错。
    /// </summary>
    public static AtlasPageData ResolvePage(AtlasData data, string? atlasImagePath = null)
    {
        if (data.Pages.Count == 0)
            throw new InvalidDataException("描述文件里没有任何图集页（pages 为空）。");

        var page = ResolvePageOrNull(data, string.IsNullOrWhiteSpace(atlasImagePath) ? null : Path.GetFileName(atlasImagePath));
        if (page is not null) return page;

        if (!string.IsNullOrWhiteSpace(atlasImagePath))
            throw new InvalidDataException(
                $"描述文件（{data.Pages.Count} 页）中没有发现匹配 “{Path.GetFileName(atlasImagePath)}” 的页，" +
                "本页只处理单张图集，请确认图集图片与描述文件配套。");

        return data.Pages[0];
    }

    private static AtlasPageData? ResolvePageOrNull(AtlasData data, string? atlasFileName)
    {
        if (!string.IsNullOrWhiteSpace(atlasFileName))
        {
            string full = atlasFileName;
            string stem = Path.GetFileNameWithoutExtension(atlasFileName);
            foreach (var p in data.Pages)
            {
                if (string.IsNullOrEmpty(p.Image)) continue;
                string img = p.Image.Replace('\\', '/');
                string imgName = img.Contains('/') ? img[(img.LastIndexOf('/') + 1)..] : img;
                if (string.Equals(imgName, full, StringComparison.OrdinalIgnoreCase)) return p;
                if (string.Equals(Path.GetFileNameWithoutExtension(imgName), stem, StringComparison.OrdinalIgnoreCase)) return p;
            }
            return null;
        }
        return data.Pages.Count == 1 ? data.Pages[0] : null;
    }

    // ==================================================================
    //  3) 拆图
    // ==================================================================

    /// <summary>
    /// 反解一张图集：把 <paramref name="atlasImagePath"/> 按描述文件里对应页的 regions 拆成小图。
    /// <paramref name="descriptionPath"/> 为空时会自动在同级目录猜测描述文件。
    /// </summary>
    public static AtlasUnpackResult Unpack(string atlasImagePath, string? descriptionPath = null, OutputFormat format = OutputFormat.Png)
    {
        if (string.IsNullOrWhiteSpace(atlasImagePath) || !File.Exists(atlasImagePath))
            throw new FileNotFoundException("图集图片不存在：" + atlasImagePath);

        descriptionPath = string.IsNullOrWhiteSpace(descriptionPath)
            ? TryFindDescriptionFile(atlasImagePath)
            : descriptionPath;
        if (string.IsNullOrWhiteSpace(descriptionPath) || !File.Exists(descriptionPath))
            throw new FileNotFoundException("找不到图集的描述文件（.atlas.txt / .atlas.json），请手动指定。");

        var data = LoadDescription(File.ReadAllText(descriptionPath), out bool isPixi);
        var page = ResolvePage(data, atlasImagePath);

        using var atlas = SKBitmap.Decode(atlasImagePath)
            ?? throw new InvalidDataException("图集图片解码失败（不是支持的图片格式？）：" + atlasImagePath);

        var result = new AtlasUnpackResult
        {
            DescriptionFile = descriptionPath,
            AtlasImage = atlasImagePath,
            PageWidth = page.Width > 0 ? page.Width : atlas.Width,
            PageHeight = page.Height > 0 ? page.Height : atlas.Height,
            FromPixiJs = isPixi,
        };

        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var region in page.Regions)
        {
            if (region.W <= 0 || region.H <= 0 || region.X < 0 || region.Y < 0 ||
                region.X >= atlas.Width || region.Y >= atlas.Height)
            {
                result.Skipped++;
                continue;
            }

            int rw = Math.Min(region.W, atlas.Width - region.X);
            int rh = Math.Min(region.H, atlas.Height - region.Y);
            int sw = region.RestoredWidth;
            int sh = region.RestoredHeight;
            if (sw <= 0 || sh <= 0 || sw > MaxSpriteDimension || sh > MaxSpriteDimension)
            {
                result.Skipped++;
                continue;
            }

            try
            {
                using var bmp = ExtractSprite(atlas, new SKRectI(region.X, region.Y, region.X + rw, region.Y + rh), region.Rotated, sw, sh);
                string fileName = UniqueFileName(SanitizeFileName(region.Name), usedNames);
                result.Sprites.Add(new UnpackedSprite
                {
                    Name = region.Name,
                    FileName = fileName,
                    Width = bmp.Width,
                    Height = bmp.Height,
                    WasRotated = region.Rotated,
                    SourceX = region.X,
                    SourceY = region.Y,
                    Bytes = EncodeBitmap(bmp, format),
                });
            }
            catch
            {
                result.Skipped++;
            }
        }

        return result;
    }

    /// <summary>
    /// 从整张图集中裁出一个区域；<paramref name="rotated"/> 为真时做逆时针 90° 还原。
    /// </summary>
    public static SKBitmap ExtractSprite(SKBitmap atlas, SKRectI rect, bool rotated, int restoreWidth, int restoreHeight)
    {
        int rw = Math.Min(rect.Width, atlas.Width - Math.Max(0, rect.Left));
        int rh = Math.Min(rect.Height, atlas.Height - Math.Max(0, rect.Top));
        if (rw <= 0 || rh <= 0) throw new InvalidDataException("要裁切的区域不在图集范围内。");

        int outW = rotated ? Math.Max(1, restoreWidth) : rw;
        int outH = rotated ? Math.Max(1, restoreHeight) : rh;

        var dst = new SKBitmap(outW, outH, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using (var canvas = new SKCanvas(dst))
        {
            canvas.Clear(SKColors.Transparent);
            if (rotated)
            {
                // 打包时是顺时针 90°（rect 里存的是转过的姿态），这里反向 90° 还原成原始朝向。
                canvas.RotateDegrees(-90, outW / 2f, outH / 2f);
                canvas.DrawBitmap(atlas,
                    new SKRect(rect.Left, rect.Top, rect.Left + rw, rect.Top + rh),
                    new SKRect(outW / 2f - rw / 2f, outH / 2f - rh / 2f, outW / 2f + rw / 2f, outH / 2f + rh / 2f),
                    new SKSamplingOptions(SKFilterMode.Nearest));
            }
            else
            {
                canvas.DrawBitmap(atlas,
                    new SKRect(rect.Left, rect.Top, rect.Left + rw, rect.Top + rh),
                    new SKRect(0, 0, outW, outH),
                    new SKSamplingOptions(SKFilterMode.Nearest));
            }
        }
        return dst;
    }

    // ==================================================================
    //  4) 写盘
    // ==================================================================

    /// <summary>
    /// 把拆好的小图写入输出文件夹，返回写入的文件数。
    /// </summary>
    public static int WriteSprites(IEnumerable<UnpackedSprite> sprites, string outputFolder, OutputFormat format = OutputFormat.Png)
    {
        if (!Directory.Exists(outputFolder)) Directory.CreateDirectory(outputFolder);

        string ext = format == OutputFormat.Webp ? ".webp" : ".png";
        int n = 0;
        foreach (var s in sprites)
        {
            if (s.Bytes.Length == 0) continue;
            string file = Path.Combine(outputFolder, Path.GetFileNameWithoutExtension(s.FileName) + ext);
            File.WriteAllBytes(file, s.Bytes);
            n++;
        }
        return n;
    }

    /// <summary>
    /// 一步到位：给定图集图片 + 描述文件，直接拆图并写入输出文件夹。
    /// </summary>
    public static AtlasUnpackResult UnpackToFolder(string atlasImagePath, string? descriptionPath, string outputFolder, OutputFormat format = OutputFormat.Png)
    {
        var result = Unpack(atlasImagePath, descriptionPath, format);
        if (!Directory.Exists(outputFolder)) Directory.CreateDirectory(outputFolder);
        string ext = ExtOf(format);
        foreach (var s in result.Sprites)
        {
            if (s.Bytes.Length == 0) continue;
            File.WriteAllBytes(Path.Combine(outputFolder, Path.GetFileNameWithoutExtension(s.FileName) + ext), s.Bytes);
        }
        return result;
    }

    /// <summary>输出格式对应的文件扩展名。</summary>
    public static string ExtOf(OutputFormat format) => format == OutputFormat.Webp ? ".webp" : ".png";

    // ==================================================================
    //  内部辅助
    // ==================================================================

    private static byte[] EncodeBitmap(SKBitmap bmp, OutputFormat format)
    {
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(format == OutputFormat.Webp ? SKEncodedImageFormat.Webp : SKEncodedImageFormat.Png, 100);
        return data?.ToArray() ?? Array.Empty<byte>();
    }

    private static string SanitizeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "sprite";
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (char c in name.Trim())
            sb.Append(c is '\\' or '/' or ':' or '*' or '?' or '"' or '<' or '>' or '|' ? '_' : c);
        string s = sb.ToString().Trim('_', '.', ' ');
        return string.IsNullOrEmpty(s) ? "sprite" : s;
    }

    private static string UniqueFileName(string baseName, HashSet<string> used)
    {
        string name = baseName;
        int k = 1;
        while (!used.Add(name))
            name = baseName + "_" + (k++);
        return name;
    }
}
