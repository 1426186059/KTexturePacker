using System.IO.Compression;
using System.Text.Json.Nodes;
using KTexturePacker.Core;
using Microsoft.AspNetCore.Http.Features;
using SkiaSharp;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// 远程上传模式需要放宽 Kestrel 请求体限制（本地路径模式不读请求体，无影响）。
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
            current = "";
            dirs = Directory.GetLogicalDrives().OrderBy(d => d).ToList();
            parent = "";
        }
        else
        {
            var di = new DirectoryInfo(path);
            if (!di.Exists)
                return Results.Text("路径不存在: " + path, "text/plain; charset=utf-8", statusCode: 400);
            current = di.FullName;
            parent = di.Parent == null ? "" : di.Parent.FullName;
            dirs = di.EnumerateDirectories()
                     .Select(d => d.FullName)
                     .OrderBy(d => d)
                     .ToList();
        }

        var arr = new JsonArray();
        foreach (var d in dirs)
            arr.Add(JsonValue.Create(d));

        var json = new JsonObject
        {
            ["current"] = current,
            ["parent"] = parent,
            ["dirs"] = arr,
        };
        return Results.Text(json.ToJsonString(), "application/json; charset=utf-8");
    }
    catch (Exception ex)
    {
        return Results.Text("读取目录失败: " + ex.Message, "text/plain; charset=utf-8", statusCode: 500);
    }
});

// 从上传的文件集合读取并解码图片，打包渲染出图集 PNG。
// 用于远程模式：图片由浏览器上传，不依赖服务器路径。
static (MemoryStream? Png, PackingResult? Result, string? Error) BuildAtlasFromFiles(
    IFormFileCollection files, PackerSettings settings)
{
    var exts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tga"
    };
    var inputs = new List<SpriteInput>();
    var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    foreach (var f in files)
    {
        var fn = Path.GetFileName(f.FileName); // webkitdirectory 的 FileName 可能含相对路径，取末段
        if (!exts.Contains(Path.GetExtension(fn)))
            continue;
        try
        {
            using var stream = f.OpenReadStream();
            var bmp = SKBitmap.Decode(stream);
            if (bmp is null)
                continue;

            var baseName = Path.GetFileNameWithoutExtension(fn);
            if (string.IsNullOrWhiteSpace(baseName))
                baseName = "sprite_" + inputs.Count;
            var name = baseName;
            int k = 1;
            while (!usedNames.Add(name))
                name = baseName + "_" + (k++);

            inputs.Add(new SpriteInput(name, bmp));
        }
        catch
        {
            continue;
        }
    }

    if (inputs.Count == 0)
    {
        var names = string.Join(", ", System.Linq.Enumerable.Select(files, f => (f.FileName + " (" + f.Length + "B)")));
        return (null, null, "没有可解码的图片。收到 " + files.Count + " 个文件：[" + names + "]。仅支持 png/jpg/gif/bmp/webp/tga，且文件需为合法图片。");
    }

    var result = AtlasPacker.Pack(inputs, settings);
    using var atlas = AtlasPacker.RenderAtlas(result);
    using var atlasData = atlas.Encode(SKEncodedImageFormat.Png, 100);
    if (atlasData is null)
    {
        foreach (var s in inputs)
            s.Bitmap.Dispose();
        return (null, null, "图集编码失败。");
    }

    var png = new MemoryStream();
    atlasData.AsStream().CopyTo(png);
    png.Position = 0;

    foreach (var s in inputs)
        s.Bitmap.Dispose();

    return (png, result, null);
}

// 从服务器本地文件夹读取图片，打包渲染出图集 PNG。
// 用于本地模式（浏览器与服务器同机）：直接扫服务器磁盘上的输入目录。
static (MemoryStream? Png, PackingResult? Result, string? Error) BuildAtlasFromFolder(
    string inputFolder, string? outputFolder, PackerSettings settings)
{
    if (string.IsNullOrWhiteSpace(inputFolder) || !Directory.Exists(inputFolder))
        return (null, null, "输入文件夹不存在: " + inputFolder);
    if (string.IsNullOrWhiteSpace(outputFolder)) outputFolder = inputFolder;

    var exts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tga"
    };
    var paths = Directory.EnumerateFiles(inputFolder)
        .Where(p => exts.Contains(Path.GetExtension(p)))
        .OrderBy(p => p)
        .ToList();

    if (paths.Count == 0)
        return (null, null, "输入文件夹里没有图片（png/jpg/gif/bmp/webp/tga）。");

    var inputs = new List<SpriteInput>();
    var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var p in paths)
    {
        try
        {
            var bmp = SKBitmap.Decode(p);
            if (bmp is null) continue;
            var baseName = Path.GetFileNameWithoutExtension(p);
            var name = baseName;
            int k = 1;
            while (!usedNames.Add(name)) name = baseName + "_" + (k++);
            inputs.Add(new SpriteInput(name, bmp));
        }
        catch { continue; }
    }

    if (inputs.Count == 0)
        return (null, null, "没有可解码的图片。");

    var result = AtlasPacker.Pack(inputs, settings);
    using var atlas = AtlasPacker.RenderAtlas(result);
    using var atlasData = atlas.Encode(SKEncodedImageFormat.Png, 100);
    if (atlasData is null)
    {
        foreach (var s in inputs) s.Bitmap.Dispose();
        return (null, null, "图集编码失败。");
    }

    if (outputFolder != inputFolder && !Directory.Exists(outputFolder))
        Directory.CreateDirectory(outputFolder);

    var imageName = "atlas.png";
    var descName = result is null ? "atlas.json" : imageName;
    var pngPath = Path.Combine(outputFolder, imageName);
    using (var fs = File.OpenWrite(pngPath)) atlasData.AsStream().CopyTo(fs);
    foreach (var s in inputs) s.Bitmap.Dispose();

    // 把生成的 PNG 读回 MemoryStream 以便 HTTP 预览/下载返回
    var png = new MemoryStream();
    using (var fs = File.OpenRead(pngPath)) fs.CopyTo(png);
    png.Position = 0;
    return (png, result, null);
}

static (PackerSettings Settings, ExportFormat Format, string? Error) ParsePackParams(IFormCollection form)
{
    int maxSize = int.TryParse(form["maxSize"], out var m) && m > 0 ? m : 2048;
    int padding = int.TryParse(form["padding"], out var p) && p >= 0 ? p : 1;
    bool allowRotation = form["allowRotation"] == "true" || form["allowRotation"] == "1";
    var algorithm = ParseAlgorithm(form["algorithm"]);
    var format = form["format"] == "libgdx" ? ExportFormat.LibGdx : ExportFormat.Json;

    var settings = new PackerSettings
    {
        MaxSize = maxSize,
        Padding = padding,
        AllowRotation = allowRotation,
        Algorithm = algorithm,
    };

    return (settings, format, null);
}

// 把图集 PNG + 描述写入服务器本地输出文件夹（本地模式）。
static string WriteAtlasToDisk(PackingResult result, MemoryStream png, string outputFolder, ExportFormat format)
{
    if (!Directory.Exists(outputFolder)) Directory.CreateDirectory(outputFolder);
    var imageName = "atlas.png";
    var descName = format == ExportFormat.LibGdx ? "atlas.atlas" : "atlas.json";
    var desc = format == ExportFormat.LibGdx
        ? AtlasExporter.ToLibGdx(result, imageName)
        : AtlasExporter.ToJson(result, imageName);

    using (var fs = File.OpenWrite(Path.Combine(outputFolder, imageName)))
    {
        png.Position = 0;
        png.CopyTo(fs);
    }
    File.WriteAllText(Path.Combine(outputFolder, descName), desc);
    return Path.Combine(outputFolder, imageName);
}

// 把图集 PNG + 描述文件打成 zip 返回（远程模式）。
static MemoryStream ZipAtlas(PackingResult result, MemoryStream png, ExportFormat format)
{
    var imageName = "atlas.png";
    var descName = format == ExportFormat.LibGdx ? "atlas.atlas" : "atlas.json";
    var desc = format == ExportFormat.LibGdx
        ? AtlasExporter.ToLibGdx(result, imageName)
        : AtlasExporter.ToJson(result, imageName);

    using var pngToZip = png;
    var zip = new MemoryStream();
    using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, true))
    {
        var entryImg = archive.CreateEntry(imageName);
        using (var zs = entryImg.Open()) { pngToZip.Position = 0; pngToZip.CopyTo(zs); }
        var entryDesc = archive.CreateEntry(descName);
        using (var zs = entryDesc.Open())
        using (var w = new StreamWriter(zs))
            w.Write(desc);
    }
    zip.Position = 0;
    return zip;
}

void SetMetaHeaders(HttpContext ctx, PackingResult result)
{
    ctx.Response.Headers["X-Atlas-Width"] = result.AtlasWidth.ToString();
    ctx.Response.Headers["X-Atlas-Height"] = result.AtlasHeight.ToString();
    ctx.Response.Headers["X-Sprite-Count"] = result.Sprites.Count.ToString();
    ctx.Response.Headers["X-Unplaced-Count"] = result.Unplaced.Count.ToString();
}

// ============ 预览 ============
// 远程模式：上传文件 -> 返回 PNG
app.MapPost("/api/preview", async (HttpContext ctx) =>
{
    IFormCollection form;
    try { form = await ctx.Request.ReadFormAsync(); }
    catch (Exception ex) { return Results.Text("读取表单失败: " + ex.Message, "text/plain; charset=utf-8", statusCode: 400); }

    if (form.Files.Count == 0)
        return Results.Text("请先上传图片文件。", "text/plain; charset=utf-8", statusCode: 400);

    var (settings, _, perr) = ParsePackParams(form);
    var (png, result, error) = BuildAtlasFromFiles(form.Files, settings);
    if (error is not null)
        return Results.Text(error, "text/plain; charset=utf-8", statusCode: 400);

    SetMetaHeaders(ctx, result!);
    return Results.File(png!, "image/png");
});

// 本地模式：输入/输出文件夹路径 -> 返回 PNG（GET，参数走 query，便于前端直接 fetch）
app.MapGet("/api/preview-local", (string? inputFolder, string? outputFolder, int? maxSize, int? padding, string? algorithm, bool? allowRotation) =>
{
    var settings = new PackerSettings
    {
        MaxSize = maxSize is > 0 ? maxSize.Value : 2048,
        Padding = padding is >= 0 ? padding.Value : 1,
        AllowRotation = allowRotation ?? false,
        Algorithm = ParseAlgorithm(algorithm),
    };
    var (png, result, error) = BuildAtlasFromFolder(inputFolder ?? "", outputFolder, settings);
    if (error is not null)
        return Results.Text(error, "text/plain; charset=utf-8", statusCode: 400);
    return Results.File(png!, "image/png");
});

// ============ 打包 ============
// 远程模式：上传文件 -> 下载 zip
app.MapPost("/api/pack", async (HttpContext ctx) =>
{
    IFormCollection form;
    try { form = await ctx.Request.ReadFormAsync(); }
    catch (Exception ex) { return Results.Text("读取表单失败: " + ex.Message, "text/plain; charset=utf-8", statusCode: 400); }

    if (form.Files.Count == 0)
        return Results.Text("请先上传图片文件。", "text/plain; charset=utf-8", statusCode: 400);

    var (settings, format, perr) = ParsePackParams(form);
    var (png, result, error) = BuildAtlasFromFiles(form.Files, settings);
    if (error is not null)
        return Results.Text(error, "text/plain; charset=utf-8", statusCode: 400);

    var zip = ZipAtlas(result!, png!, format);
    return Results.File(zip, "application/zip", "atlas.zip");
});

// 本地模式：输入/输出文件夹路径 -> 写入服务器磁盘（GET，参数走 query）
app.MapGet("/api/pack-local", (string? inputFolder, string? outputFolder, int? maxSize, int? padding, string? algorithm, bool? allowRotation, string? format) =>
{
    if (string.IsNullOrWhiteSpace(outputFolder))
        return Results.Text("本地模式需要填写输出文件夹。", "text/plain; charset=utf-8", statusCode: 400);

    var settings = new PackerSettings
    {
        MaxSize = maxSize is > 0 ? maxSize.Value : 2048,
        Padding = padding is >= 0 ? padding.Value : 1,
        AllowRotation = allowRotation ?? false,
        Algorithm = ParseAlgorithm(algorithm),
    };
    var fmt = format == "libgdx" ? ExportFormat.LibGdx : ExportFormat.Json;
    var (png, result, error) = BuildAtlasFromFolder(inputFolder ?? "", outputFolder, settings);
    if (error is not null)
        return Results.Text(error, "text/plain; charset=utf-8", statusCode: 400);

    var written = WriteAtlasToDisk(result!, png!, outputFolder!, fmt);
    return Results.Text("已生成图集：\n" + written, "text/plain; charset=utf-8");
});

app.Run();
