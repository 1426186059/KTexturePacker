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

app.UseDefaultFiles();
app.UseStaticFiles();

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
                 .Where(f => IsImageFile(f))
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

// 把所有页的 PNG 流上下拼成一张预览大图（页间留 8px 透明缝）。
// 注意：必须从已渲染的 PNG 流拼合，不能从 pages 重新绘制——源 SKBitmap 在 BuildAtlas* 里已释放。
static MemoryStream RenderComposite(IReadOnlyList<MemoryStream> pngs)
{
    var bmps = new List<SKBitmap>();
    foreach (var ms in pngs) { ms.Position = 0; bmps.Add(SKBitmap.Decode(ms)!); }

    if (bmps.Count == 1)
    {
        using var b = bmps[0];
        using var data = b.Encode(SKEncodedImageFormat.Png, 100);
        var single = new MemoryStream();
        data!.AsStream().CopyTo(single);
        single.Position = 0;
        return single;
    }

    const int gap = 8;
    int totalH = bmps.Sum(b => b.Height) + gap * (bmps.Count - 1);
    int maxW = bmps.Max(b => b.Width);
    using var atlas = new SKBitmap(maxW, totalH);
    using var canvas = new SKCanvas(atlas);
    canvas.Clear(SKColors.Transparent);
    int y = 0;
    foreach (var b in bmps)
    {
        canvas.DrawBitmap(b, 0, y, new SKSamplingOptions(SKFilterMode.Linear));
        y += b.Height + gap;
    }
    foreach (var b in bmps) b.Dispose();

    using var data2 = atlas.Encode(SKEncodedImageFormat.Png, 100);
    var msOut = new MemoryStream();
    data2!.AsStream().CopyTo(msOut);
    msOut.Position = 0;
    return msOut;
}

// 写入服务器磁盘：atlas_0.png … atlas_N-1.png + 单个 atlas.atlas.json（含所有 page）
static string WriteAtlasToDisk(List<PackingResult> pages, List<MemoryStream> pngs, string outputFolder)
{
    if (!Directory.Exists(outputFolder)) Directory.CreateDirectory(outputFolder);
    var imageNames = new List<string>();
    for (int i = 0; i < pages.Count; i++)
    {
        var imgName = "atlas_" + i + ".png";
        imageNames.Add(imgName);
        var pngPath = Path.Combine(outputFolder, imgName);
        using (var fs = File.OpenWrite(pngPath)) { pngs[i].Position = 0; pngs[i].CopyTo(fs); }
    }
    var desc = AtlasExporter.ToAtlas(pages, imageNames);
    File.WriteAllText(Path.Combine(outputFolder, "atlas.atlas.json"), desc);
    return Path.Combine(outputFolder, "atlas.atlas.json");
}

// 打包成 zip：atlas_0.png … + 单个 atlas.atlas.json
static MemoryStream ZipAtlas(List<PackingResult> pages, List<MemoryStream> pngs)
{
    var imageNames = new List<string>();
    for (int i = 0; i < pages.Count; i++) imageNames.Add("atlas_" + i + ".png");
    var desc = AtlasExporter.ToAtlas(pages, imageNames);

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
        var descEntry = archive.CreateEntry("atlas.atlas.json");
        using (var zs = descEntry.Open())
        using (var w = new StreamWriter(zs))
            w.Write(desc);
    }
    zip.Position = 0;
    return zip;
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

// 上传 -> 直接返回拼合预览 PNG（多页拼成一张）
app.MapPost("/api/preview", async (HttpContext ctx) =>
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

    SetMetaHeaders(ctx, pages);
    var composite = RenderComposite(pngs);
    foreach (var p in pngs) p.Dispose();
    return Results.File(composite, "image/png");
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

    var zip = ZipAtlas(pages, pngs);
    foreach (var p in pngs) p.Dispose();
    return Results.File(zip, "application/zip", "atlas.zip");
});

// ---------- 本地模式：文件夹路径 ----------

// 本地模式：输入/输出文件夹路径 -> 返回拼合预览 PNG（GET，参数走 query，便于前端直接 fetch）
app.MapGet("/api/preview-local", (HttpContext ctx, string? inputFolder, string? outputFolder, int? maxSize, int? padding, string? algorithm, bool? allowRotation) =>
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
    var composite = RenderComposite(pngs);
    foreach (var p in pngs) p.Dispose();
    return Results.File(composite, "image/png");
});

// 本地模式：输入/输出文件夹路径 -> 写入服务器磁盘（GET，参数走 query）
app.MapGet("/api/pack-local", (string? inputFolder, string? outputFolder, int? maxSize, int? padding, string? algorithm, bool? allowRotation) =>
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

    var atlasPath = WriteAtlasToDisk(pages, pngs, outputFolder!);
    foreach (var p in pngs) p.Dispose();
    return Results.Text("已生成图集：" + atlasPath + "（" + pages.Count + " 页）", "text/plain; charset=utf-8");
});

app.Run();
