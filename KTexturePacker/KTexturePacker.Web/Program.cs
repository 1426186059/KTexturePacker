using System.IO.Compression;
using System.Text.Json.Nodes;
using KTexturePacker.Core;
using Microsoft.AspNetCore.Http.Features;
using SkiaSharp;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// 上传可能很大，禁用请求体大小限制
app.Use(async (context, next) =>
{
    var maxReq = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
    if (maxReq is not null) maxReq.MaxRequestBodySize = null;
    await next();
});

// 所有 /api 响应禁止缓存：避免浏览器缓存预览/打包结果导致「再次点击无效」或参数修改不生效。
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

static PackerSettings ParseSettings(string? maxSize, string? padding, string? algorithm, bool? allowRotation)
{
    return new PackerSettings
    {
        MaxSize = int.TryParse(maxSize, out var m) && m > 0 ? m : 2048,
        Padding = int.TryParse(padding, out var p) && p >= 0 ? p : 1,
        AllowRotation = allowRotation ?? false,
        Algorithm = ParseAlgorithm(algorithm),
    };
}

// 自动判定模式：服务器是否和浏览器同机。
// 本地模式本质是「服务器按磁盘路径读盘」，只有服务器在本机（localhost / 回环连接）时才有意义。
static bool IsLocalRequest(HttpContext ctx)
{
    var host = ctx.Request.Host.Host;
    if (host == "localhost" || host == "127.0.0.1" || host == "::1" || host == "[::1]")
        return true;
    var remote = ctx.Connection.RemoteIpAddress;
    if (remote is not null && (System.Net.IPAddress.IsLoopback(remote)))
        return true;
    return false;
}

// 返回 { local: true/false }，前端据此自动选择默认模式并决定是否禁用本地模式。
app.MapGet("/api/mode", (HttpContext ctx) =>
{
    var json = new JsonObject { ["local"] = IsLocalRequest(ctx) };
    return Results.Text(json.ToJsonString(), "application/json; charset=utf-8");
});

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

static (List<MemoryStream> Pngs, List<PackingResult> Pages, string? Error) BuildAtlasFromFiles(
    IEnumerable<IFormFile> files, PackerSettings settings)
{
    var inputs = new List<SpriteInput>();
    var names = new List<string>();
    foreach (var f in files)
    {
        if (!IsImageFile(f.FileName)) continue;
        string baseName = Path.GetFileNameWithoutExtension(f.FileName);
        string name = baseName;
        int k = 1;
        while (names.Contains(name)) { name = baseName + "_" + (k++); }
        names.Add(name);
        using var ms = new MemoryStream();
        f.CopyTo(ms);
        ms.Position = 0;
        var bmp = SKBitmap.Decode(ms);
        if (bmp is null) continue;
        inputs.Add(new SpriteInput(name, bmp));
    }

    if (inputs.Count == 0)
    {
        var fileList = string.Join(", ", System.Linq.Enumerable.Select(files, f => f.FileName + " (" + f.Length + "B)"));
        return (null!, null!, "没有可解码的图片。收到 " + System.Linq.Enumerable.Count(files) + " 个文件：[" + fileList + "]。仅支持 png/jpg/gif/bmp/webp/tga，且文件需为合法图片。");
    }

    var pages = AtlasPacker.PackPages(inputs, settings);
    var (pngs, error) = RenderPages(pages);
    foreach (var s in inputs) s.Bitmap.Dispose();
    return (pngs, pages, error);
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

// 预览：把每个图集页按「预览最大边长」切分成多张预览页（分页/平铺），每页不超过 previewMax×previewMax。
// 若某图集页本身不超过 previewMax，则整页作为 1 张预览页输出（原始尺寸，不放大）。
// 这样「预览最大边长」即可控制预览的分页数量：值越小，预览页越多。
// 注意：必须从已渲染的 PNG 流切分，不能从 pages 重新绘制——源 SKBitmap 在 BuildAtlas* 里已释放。
static JsonObject RenderPreviewPages(IReadOnlyList<MemoryStream> pngs, int previewMax)
{
    previewMax = Math.Clamp(previewMax, 64, 4096);
    var arr = new JsonArray();
    foreach (var ms in pngs)
    {
        ms.Position = 0;
        using var bmp = SKBitmap.Decode(ms)!;
        int w = bmp.Width, h = bmp.Height;

        // 整页不超上限：直接输出原尺寸单页
        if (w <= previewMax && h <= previewMax)
        {
            using var img = SKImage.FromBitmap(bmp);
            using var data = img.Encode(SKEncodedImageFormat.Png, 100);
            arr.Add((JsonNode)new JsonObject
            {
                ["w"] = w,
                ["h"] = h,
                ["png"] = Convert.ToBase64String(data.ToArray()),
            });
            continue;
        }

        // 切分为 previewMax×previewMax 的网格，每格作为一页预览
        int cols = (int)Math.Ceiling((double)w / previewMax);
        int rows = (int)Math.Ceiling((double)h / previewMax);
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                int sx = c * previewMax;
                int sy = r * previewMax;
                int sw = Math.Min(previewMax, w - sx);
                int sh = Math.Min(previewMax, h - sy);
                using var tile = new SKBitmap(sw, sh);
                using (var canvas = new SKCanvas(tile))
                {
                    canvas.Clear(SKColors.Transparent);
                    canvas.DrawBitmap(bmp,
                        new SKRect(sx, sy, sx + sw, sy + sh),
                        new SKRect(0, 0, sw, sh),
                        new SKSamplingOptions(SKFilterMode.Linear));
                }
                using var img = SKImage.FromBitmap(tile);
                using var data = img.Encode(SKEncodedImageFormat.Png, 100);
                arr.Add((JsonNode)new JsonObject
                {
                    ["w"] = sw,
                    ["h"] = sh,
                    ["png"] = Convert.ToBase64String(data.ToArray()),
                });
            }
        }
    }
    return new JsonObject { ["pages"] = arr, ["count"] = arr.Count };
}

// 写入服务器磁盘：{atlasName}_0.png … + 单个 {atlasName}AtlasConst.Atlas_Extention（含所有 page）
static string WriteAtlasToDisk(List<PackingResult> pages, List<MemoryStream> pngs, string outputFolder, string atlasName)
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
    var desc = AtlasExporter.ToJson(pages, imageNames);
    string descPath = Path.Combine(outputFolder, atlasName + AtlasConst.Atlas_Extention);
    File.WriteAllText(descPath, desc);
    return descPath;
}

// 打包成 zip：{atlasName}_0.png … + 单个 {atlasName}AtlasConst.Atlas_Extention，返回 zip 流与下载文件名
static (MemoryStream zip, string fileName) ZipAtlas(List<PackingResult> pages, List<MemoryStream> pngs, string atlasName)
{
    var imageNames = new List<string>();
    for (int i = 0; i < pages.Count; i++) imageNames.Add(atlasName + "_" + i + ".png");
    var desc = AtlasExporter.ToJson(pages, imageNames);
    var libgdx = AtlasExporter.ToAtlas(pages, imageNames);

    var zip = new MemoryStream();
    using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, true))
    {
        for (int i = 0; i < pages.Count; i++)
        {
            var entry = archive.CreateEntry(imageNames[i]);
            using var zs = entry.Open();
            pngs[i].Position = 0;
            pngs[i].CopyTo(zs);
        }
        var descEntry = archive.CreateEntry(atlasName + AtlasConst.Atlas_Extention);
        using (var zs = descEntry.Open())
        using (var w = new StreamWriter(zs))
            w.Write(desc);
    }
    zip.Position = 0;
    return (zip, atlasName + ".zip");
}

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

// ---------- 远端模式：上传 ----------

// 上传 -> 返回分页缩略图 JSON（按 previewMax 切分为多页预览）
app.MapPost("/api/preview", async (HttpContext ctx) =>
{
    IFormCollection form;
    try { form = await ctx.Request.ReadFormAsync(); }
    catch (Exception ex) { return Results.Text("读取表单失败: " + ex.Message, "text/plain; charset=utf-8", statusCode: 400); }

    if (form.Files.Count == 0)
        return Results.Text("请先上传图片文件。", "text/plain; charset=utf-8", statusCode: 400);

    var settings = ParseSettings(form["maxSize"], form["padding"], form["algorithm"], form["allowRotation"] == "true" || form["allowRotation"] == "1");
    int previewMax = int.TryParse(form["previewMax"], out var pm) && pm > 0 ? pm : 512;
    var (pngs, pages, error) = BuildAtlasFromFiles(form.Files, settings);
    if (error is not null)
        return Results.Text(error, "text/plain; charset=utf-8", statusCode: 400);

    SetMetaHeaders(ctx, pages);
    var json = RenderPreviewPages(pngs, previewMax);
    foreach (var p in pngs) p.Dispose();
    return Results.Text(json.ToJsonString(), "application/json; charset=utf-8");
});

// 上传 -> 打包成 zip 下载
app.MapPost("/api/pack", async (HttpContext ctx) =>
{
    IFormCollection form;
    try { form = await ctx.Request.ReadFormAsync(); }
    catch (Exception ex) { return Results.Text("读取表单失败: " + ex.Message, "text/plain; charset=utf-8", statusCode: 400); }

    if (form.Files.Count == 0)
        return Results.Text("请先上传图片文件。", "text/plain; charset=utf-8", statusCode: 400);

    var settings = ParseSettings(form["maxSize"], form["padding"], form["algorithm"], form["allowRotation"] == "true" || form["allowRotation"] == "1");
    var (pngs, pages, error) = BuildAtlasFromFiles(form.Files, settings);
    if (error is not null)
        return Results.Text(error, "text/plain; charset=utf-8", statusCode: 400);

    var unplaced = pages.Sum(p => p.Unplaced.Count);
    if (unplaced > 0)
    {
        foreach (var p in pngs) p.Dispose();
        return Results.Text($"有 {unplaced} 张图片无法放入（可能单张超过最大边长），请调大 maxSize 或拆分。", "text/plain; charset=utf-8", statusCode: 400);
    }

    var atlasName = ResolveAtlasName(form["atlasName"], null, null);
    var (zip, fileName) = ZipAtlas(pages, pngs, atlasName);
    foreach (var p in pngs) p.Dispose();
    return Results.File(zip, "application/zip", fileName);
});

// ---------- 本地模式：文件夹路径 ----------

// 本地模式：输入/输出文件夹路径 -> 返回分页缩略图 JSON（按 previewMax 切分为多页预览，GET 便于前端直接 fetch）
app.MapGet("/api/preview-local", (HttpContext ctx, string? inputFolder, string? outputFolder, int? maxSize, int? padding, string? algorithm, bool? allowRotation, int? previewMax) =>
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
    int pv = previewMax is > 0 ? previewMax.Value : 512;
    var json = RenderPreviewPages(pngs, pv);
    foreach (var p in pngs) p.Dispose();
    return Results.Text(json.ToJsonString(), "application/json; charset=utf-8");
});

// 本地模式：输入/输出文件夹路径 -> 写入服务器磁盘（GET，参数走 query）
app.MapGet("/api/pack-local", (HttpContext ctx, string? inputFolder, string? outputFolder, int? maxSize, int? padding, string? algorithm, bool? allowRotation) =>
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

    var unplaced = pages.Sum(p => p.Unplaced.Count);
    if (unplaced > 0)
    {
        foreach (var p in pngs) p.Dispose();
        return Results.Text($"有 {unplaced} 张图片无法放入（可能单张超过最大边长），请调大 maxSize 或拆分。", "text/plain; charset=utf-8", statusCode: 400);
    }

    var atlasName = ResolveAtlasName(ctx.Request.Query["atlasName"], outputFolder, inputFolder);
    var atlasPath = WriteAtlasToDisk(pages, pngs, outputFolder!, atlasName);
    foreach (var p in pngs) p.Dispose();
    return Results.Text("已生成图集：" + atlasPath + "（" + pages.Count + " 页，前缀 " + atlasName + "）", "text/plain; charset=utf-8");
});

app.Run();
