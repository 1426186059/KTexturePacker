using System.Text.Json.Nodes;
using KTexturePacker.Core;
using SkiaSharp;

// ============================================================================
//  KTexturePacker.Web —— 只支持本地模式（LOCAL-ONLY）
//  本服务只接受「磁盘文件夹路径」输入，直接在服务器本机读图、打包、写盘。
//  不支持任何远程文件上传（无 multipart / 无 zip 下载端点）。
//  必须在运行服务的同一台机器上打开浏览器使用（localhost）。
// ============================================================================

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// 所有 /api 响应禁止缓存：避免浏览器缓存预览/打包结果导致「再次点击无效」或参数修改不失效。
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/api"))
        ctx.Response.Headers.CacheControl = "no-store";
    await next();
});

app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    // 禁用缓存，避免浏览器沿用旧版 index.html（旧前端把响应当 PNG，新后端返回 JSON 会导致预览空白）
    OnPrepareResponse = ctx =>
    {
        ctx.Context.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
        ctx.Context.Response.Headers.Pragma = "no-cache";
        ctx.Context.Response.Headers.Expires = "0";
    }
});

static MaxRectsMethod ParseAlgorithm(string? s) => s switch
{
    "long" => MaxRectsMethod.BestLongSideFit,
    "bottomleft" => MaxRectsMethod.BottomLeftRule,
    "contact" => MaxRectsMethod.ContactPointRule,
    _ => MaxRectsMethod.BestShortSideFit,
};

// 服务器端目录浏览：供本地模式的「浏览…」选择器逐级列出服务器（或本机）目录。
// 返回 JsonObject{current,parent,dirs[]}，全程用 JsonNode，原生 AOT 安全。
app.MapGet("/api/dirs", (string? path) =>
{
    try
    {
        string current;
        System.Collections.Generic.List<string> dirs;
        string parent;
        if (string.IsNullOrWhiteSpace(path))
        {
            var roots = System.IO.DriveInfo.GetDrives()
                .Where(d => d.IsReady)
                .Select(d => d.RootDirectory.FullName)
                .ToList();
            current = "";
            parent = "";
            dirs = roots;
        }
        else
        {
            var dir = new DirectoryInfo(path);
            if (!dir.Exists) return Results.Text("目录不存在: " + path, "text/plain; charset=utf-8", statusCode: 400);
            current = dir.FullName;
            parent = dir.Parent?.FullName ?? "";
            dirs = dir.EnumerateDirectories()
                .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .Select(d => d.FullName)
                .ToList();
        }
        var json = new JsonObject
        {
            ["current"] = current,
            ["parent"] = parent,
            ["dirs"] = new JsonArray(dirs.Select(d => (JsonNode)d).ToArray()),
        };
        return Results.Text(json.ToJsonString(), "application/json; charset=utf-8");
    }
    catch (Exception ex)
    {
        return Results.Text("读取目录失败: " + ex.Message, "text/plain; charset=utf-8", statusCode: 500);
    }
});

// ---------- 图集构建（多页） ----------

static bool IsImageFile(string name)
{
    var ext = Path.GetExtension(name).ToLowerInvariant();
    return ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp" or ".tga";
}

// 排除本工具自己导出的图集文件（atlas.png / atlas_0.png …），避免「输出目录=输入目录」时
// 上一轮产物被当成素材再次喂入，导致图集越滚越大、出现大量空白页。
static bool IsToolOutput(string name)
{
    if (name.Equals("atlas.png", StringComparison.OrdinalIgnoreCase)) return true;
    return System.Text.RegularExpressions.Regex.IsMatch(name, @"^atlas_\d+\.png$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
}

// 清理图集名字中的文件系统非法字符，避免写盘失败。
static string SanitizeAtlasName(string name)
{
    if (string.IsNullOrWhiteSpace(name)) return "";
    var sb = new System.Text.StringBuilder(name.Length);
    foreach (char c in name.Trim())
    {
        // Windows 文件名非法字符：\ / : * ? " < > |
        if (c is '\\' or '/' or ':' or '*' or '?' or '"' or '<' or '>' or '|')
            sb.Append('_');
        else
            sb.Append(c);
    }
    string s = sb.ToString().Trim('_', '.', ' ');
    return s.Length == 0 ? "" : s;
}

// 解析最终使用的图集名（前缀）：优先用用户给定值；否则取输出/输入文件夹的最后一段目录名；兜底 "atlas"。
static string ResolveAtlasName(string? given, string? outputFolder, string? inputFolder)
{
    string? name = string.IsNullOrWhiteSpace(given) ? null : SanitizeAtlasName(given);
    if (string.IsNullOrEmpty(name))
    {
        string? src = !string.IsNullOrWhiteSpace(outputFolder) ? outputFolder
                   : !string.IsNullOrWhiteSpace(inputFolder) ? inputFolder : null;
        if (!string.IsNullOrWhiteSpace(src))
        {
            var di = new DirectoryInfo(src.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            name = SanitizeAtlasName(di.Name);
        }
    }
    return string.IsNullOrEmpty(name) ? "atlas" : name;
}

static (List<MemoryStream> Pngs, List<PackingResult> Pages, string? Error) BuildAtlasFromFolder(
    string inputFolder, string? outputFolder, PackerSettings settings)
{
    if (string.IsNullOrWhiteSpace(inputFolder) || !Directory.Exists(inputFolder))
        return (null!, null!, "输入文件夹不存在: " + (inputFolder ?? ""));
    if (string.IsNullOrWhiteSpace(outputFolder)) outputFolder = inputFolder;

    var inputs = new List<SpriteInput>();
    var names = new List<string>();
    foreach (var file in Directory.EnumerateFiles(inputFolder)
                 .Where(f => IsImageFile(f) && !IsToolOutput(Path.GetFileName(f)))
                 .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
    {
        string baseName = Path.GetFileNameWithoutExtension(file);
        string name = baseName;
        int k = 1;
        while (names.Contains(name)) { name = baseName + "_" + (k++); }
        names.Add(name);
        var bmp = SKBitmap.Decode(file);
        if (bmp is null) continue;
        inputs.Add(new SpriteInput(name, bmp));
    }

    if (inputs.Count == 0)
        return (null!, null!, "该文件夹下没有可解码的图片（仅支持 png/jpg/gif/bmp/webp/tga）。");

    var pages = AtlasPacker.PackPages(inputs, settings);
    var (pngs, error) = RenderPages(pages);
    foreach (var s in inputs) s.Bitmap.Dispose();
    return (pngs, pages, error);
}

// 把每页打包结果渲染成 PNG 流（每张图一个 MemoryStream）
static (List<MemoryStream> Pngs, string? Error) RenderPages(List<PackingResult> pages)
{
    var pngs = new List<MemoryStream>();
    foreach (var page in pages)
    {
        using var atlas = AtlasPacker.RenderAtlas(page);
        using var data = atlas.Encode(SKEncodedImageFormat.Png, 100);
        if (data is null)
        {
            foreach (var p in pngs) p.Dispose();
            return (null!, "图集编码失败。");
        }
        var ms = new MemoryStream();
        data.AsStream().CopyTo(ms);
        ms.Position = 0;
        pngs.Add(ms);
    }
    return (pngs, null);
}

// 预览：把每个图集页整张按比例缩放到「最长边 ≤ previewMax」输出（保持原图比例，不切分）。
// 每页预览都附带其对应真实图集页的尺寸（realW/realH），便于前端区分「预览尺寸」与「实际图集尺寸」。
// 注意：必须从已渲染的 PNG 流缩放，不能从 pages 重新绘制——源 SKBitmap 在 BuildAtlas* 里已释放。
static JsonObject RenderPreviewPages(IReadOnlyList<MemoryStream> pngs, int previewMax, IReadOnlyList<PackingResult> realPages)
{
    previewMax = Math.Clamp(previewMax, 64, 4096);
    var arr = new JsonArray();
    var realArr = new JsonArray();
    for (int pi = 0; pi < pngs.Count; pi++)
    {
        int realW = realPages[pi].AtlasWidth, realH = realPages[pi].AtlasHeight;
        realArr.Add((JsonNode)new JsonObject { ["w"] = realW, ["h"] = realH });

        var ms = pngs[pi];
        ms.Position = 0;
        using var bmp = SKBitmap.Decode(ms)!;
        int w = bmp.Width, h = bmp.Height;

        // 整张按比例缩放到最长边 ≤ previewMax
        int tw = w, th = h;
        int longest = Math.Max(w, h);
        if (longest > previewMax)
        {
            double scale = (double)previewMax / longest;
            tw = Math.Max(1, (int)Math.Round(w * scale));
            th = Math.Max(1, (int)Math.Round(h * scale));
        }
        using var scaled = new SKBitmap(tw, th);
        using (var canvas = new SKCanvas(scaled))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.DrawBitmap(bmp,
                new SKRect(0, 0, w, h),
                new SKRect(0, 0, tw, th),
                new SKSamplingOptions(SKFilterMode.Linear));
        }
        using var img = SKImage.FromBitmap(scaled);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        arr.Add((JsonNode)new JsonObject
        {
            ["w"] = tw,
            ["h"] = th,
            ["realW"] = realW,
            ["realH"] = realH,
            ["page"] = pi + 1,
            ["png"] = Convert.ToBase64String(data.ToArray()),
        });
    }
    return new JsonObject { ["pages"] = arr, ["count"] = arr.Count, ["realPages"] = realArr };
}

// 写入服务器磁盘：{atlasName}_0.png … + 单个描述 JSON（后缀由 AtlasConst.JsonExtension(format) 决定，PixiJS=.pixi.json，其余=.atlas.txt）
static string WriteAtlasToDisk(List<PackingResult> pages, List<MemoryStream> pngs, string outputFolder, string atlasName, AtlasFormat format)
{
    if (!Directory.Exists(outputFolder)) Directory.CreateDirectory(outputFolder);
    var imageNames = new List<string>();
    for (int i = 0; i < pages.Count; i++)
    {
        var imgName = atlasName + "_" + i + ".png";
        imageNames.Add(imgName);
        var pngPath = Path.Combine(outputFolder, imgName);
        using (var fs = File.OpenWrite(pngPath)) { pngs[i].Position = 0; pngs[i].CopyTo(fs); }
    }
    var desc = AtlasExporter.ToJson(pages, imageNames, format);
    string descPath = Path.Combine(outputFolder, atlasName + AtlasConst.JsonExtension(format));
    File.WriteAllText(descPath, desc);
    return descPath;
}

static AtlasFormat ParseFormat(string? s) => (s ?? "") switch
{
    "pixijs" or "pixi" => AtlasFormat.PixiJS,
    _ => AtlasFormat.Generic,
};

void SetMetaHeaders(HttpContext ctx, IReadOnlyList<PackingResult> pages)
{
    var first = pages[0];
    int total = 0, unplaced = 0;
    foreach (var p in pages) { total += p.Sprites.Count; unplaced += p.Unplaced.Count; }
    ctx.Response.Headers["X-Atlas-Width"] = first.AtlasWidth.ToString();
    ctx.Response.Headers["X-Atlas-Height"] = first.AtlasHeight.ToString();
    ctx.Response.Headers["X-Sprite-Count"] = total.ToString();
    ctx.Response.Headers["X-Unplaced-Count"] = unplaced.ToString();
    ctx.Response.Headers["X-Page-Count"] = pages.Count.ToString();
}

// ---------- 本地模式：文件夹路径 ----------

// 本地模式：输入/输出文件夹路径 -> 返回分页缩略图 JSON（按固定预览最大边长切分为多页预览，GET 便于前端直接 fetch）
app.MapGet("/api/preview", (HttpContext ctx, string? inputFolder, string? outputFolder, int? maxSize, int? padding, string? algorithm, bool? allowRotation) =>
{
    var settings = new PackerSettings
    {
        MaxSize = maxSize is > 0 ? maxSize.Value : 2048,
        Padding = padding is >= 0 ? padding.Value : 1,
        AllowRotation = allowRotation ?? false,
        Algorithm = ParseAlgorithm(algorithm),
    };

    var (pngs, pages, error) = BuildAtlasFromFolder(inputFolder ?? "", outputFolder, settings);
    if (error is not null)
        return Results.Text(error, "text/plain; charset=utf-8", statusCode: 400);

    SetMetaHeaders(ctx, pages);
    ctx.Response.Headers["X-Atlas-Name"] = ResolveAtlasName(ctx.Request.Query["atlasName"], outputFolder, inputFolder);
    const int previewMax = 512; // 预览最大边长固定写死，不再由前端传参
    var json = RenderPreviewPages(pngs, previewMax, pages);
    foreach (var p in pngs) p.Dispose();
    return Results.Text(json.ToJsonString(), "application/json; charset=utf-8");
});

// 本地模式：输入/输出文件夹路径 -> 写入服务器磁盘（GET，参数走 query）
app.MapGet("/api/pack", (HttpContext ctx, string? inputFolder, string? outputFolder, int? maxSize, int? padding, string? algorithm, bool? allowRotation, string? format) =>
{
    var settings = new PackerSettings
    {
        MaxSize = maxSize is > 0 ? maxSize.Value : 2048,
        Padding = padding is >= 0 ? padding.Value : 1,
        AllowRotation = allowRotation ?? false,
        Algorithm = ParseAlgorithm(algorithm),
    };
    var fmt = ParseFormat(format);

    var (pngs, pages, error) = BuildAtlasFromFolder(inputFolder ?? "", outputFolder, settings);
    if (error is not null)
        return Results.Text(error, "text/plain; charset=utf-8", statusCode: 400);

    var unplaced = pages.Sum(p => p.Unplaced.Count);
    if (unplaced > 0)
    {
        foreach (var p in pngs) p.Dispose();
        return Results.Text($"有 {unplaced} 张图片无法放入（可能单张超过最大边长），请调大 maxSize 或拆分。", "text/plain; charset=utf-8", statusCode: 400);
    }

    var atlasName = ResolveAtlasName(ctx.Request.Query["atlasName"], outputFolder, inputFolder);
    var atlasPath = WriteAtlasToDisk(pages, pngs, outputFolder!, atlasName, fmt);
    foreach (var p in pngs) p.Dispose();
    return Results.Text("已生成图集：" + atlasPath + "（" + pages.Count + " 页，前缀 " + atlasName + "）", "text/plain; charset=utf-8");
});

app.Run();
