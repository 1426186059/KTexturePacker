using System.Text.Json.Nodes;
using KTexturePacker.Core;
using KTexturePacker.Core.JsonFormat;
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
            if (!dir.Exists) return Results.Text("目录不存在：" + FullPathOf(path), "text/plain; charset=utf-8", statusCode: 400);
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
    if (name.Equals("atlas.webp", StringComparison.OrdinalIgnoreCase)) return true;
    return System.Text.RegularExpressions.Regex.IsMatch(name, @"^atlas_\d+\.(png|webp)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
}

// 描述文件名 → 图集名（去掉 .atlas / .json / .txt 等所有扩展名，如 hero.atlas.txt → hero）
static string DescriptionStem(string file)
{
    string name = Path.GetFileNameWithoutExtension(file);
    while (true)
    {
        var ext = Path.GetExtension(name);
        if (string.IsNullOrEmpty(ext)) break;
        name = name[..^ext.Length];
    }
    return string.IsNullOrEmpty(name) ? "atlas" : name;
}

static bool IsDescriptionCandidate(string file)
{
    var ext = Path.GetExtension(file).ToLowerInvariant();
    return ext is ".json" or ".txt" or ".atlas" || file.EndsWith(".atlas.txt", StringComparison.OrdinalIgnoreCase);
}

// 出错时把路径补全（用户填的相对路径/末尾带斜杠等），保证提示里给的是完整路径、能直接复制定位。
static string FullPathOf(string? path)
{
    if (string.IsNullOrWhiteSpace(path)) return "（空）";
    try { return Path.GetFullPath(path); }
    catch { return path; }
}

// 收集「本文件夹里已被描述文件声明为图集页」的图片名 —— 这些是图集产物，不能再当素材喂进去，
// 否则图集会越滚越大。
// 注意：只排除描述文件**真正引用**的图集页，绝不按「图集名 + 数字」这种宽松规则排除，
// 否则会把拆包出来的散图（如 Map_1.png、Map_2.png…）误当图集页清掉，导致「明明有图却报没有图片」。
static HashSet<string> CollectAtlasOutputImages(string folder)
{
    var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    if (!Directory.Exists(folder)) return set;

    foreach (var desc in Directory.EnumerateFiles(folder))
    {
        if (!IsDescriptionCandidate(desc)) continue;

        AtlasData data;
        try { data = AtlasUnpacker.LoadDescription(File.ReadAllText(desc), out _); }
        catch { continue; }          // 不是图集描述文件
        if (data.Pages.Count == 0) continue;

        string stem = DescriptionStem(desc);
        for (int i = 0; i < data.Pages.Count; i++)
        {
            var img = data.Pages[i].Image;
            if (!string.IsNullOrWhiteSpace(img))
                set.Add(Path.GetFileName(img.Replace('\\', '/')));
            // 描述里的 image 名与实际文件不一致时的兜底（image=atlas_0.png、文件 Map_0.png）
            set.Add(stem + "_" + i + ".png");
            set.Add(stem + "_" + i + ".webp");
        }
    }
    return set;
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

// 从文件夹读图，交给共享核心 KTexturePacker.Core.AtlasBaker 完成「装箱 + 合成 + 编码 + 导出」，
// 返回统一的 AtlasBakeResult（每页图像字节 + 通用格式 AtlasData JSON + 用于 PixiJS 的 AutoPages）。
static (AtlasBaker.AtlasBakeResult Result, string? Error) BuildAtlasFromFolder(
    string inputFolder, string? outputFolder, PackerSettings settings, AtlasBakeOptions bakeOptions)
{
    if (string.IsNullOrWhiteSpace(inputFolder) || !Directory.Exists(inputFolder))
        return (null!, "输入文件夹不存在：" + FullPathOf(inputFolder));
    if (string.IsNullOrWhiteSpace(outputFolder)) outputFolder = inputFolder;

    // 本文件夹里已被描述文件声明为「图集页」的图片 = 图集产物，不作为素材喂入
    var atlasOutputs = CollectAtlasOutputImages(inputFolder);
    int excludedAsAtlas = 0;

    var inputs = new List<SpriteInput>();
    var names = new List<string>();
    foreach (var file in Directory.EnumerateFiles(inputFolder)
                 .Where(f => IsImageFile(f) && !IsToolOutput(Path.GetFileName(f)))
                 .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
    {
        if (atlasOutputs.Contains(Path.GetFileName(file))) { excludedAsAtlas++; continue; }

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
    {
        // 图片其实存在、但全部是图集产物时，给出能看懂的提示，而不是笼统的「没有可解码的图片」
        return excludedAsAtlas > 0
            ? (null!, $"该文件夹里的 {excludedAsAtlas} 张图片都是图集页（已被描述文件声明为图集图片），没有可作为素材的小图。\n" +
                      "请换一个装散图的文件夹，或先删掉旧的图集产物（*.atlas.txt / *.atlas.json 与其图集页）再打包。\n" +
                      "文件夹：" + FullPathOf(inputFolder))
            : (null!, "该文件夹下没有可解码的图片（仅支持 png/jpg/gif/bmp/webp/tga）。\n文件夹：" + FullPathOf(inputFolder));
    }

    var result = AtlasBaker.Bake(inputs, null, settings, bakeOptions);
    return (result, null);
}

// 预览：把每个图集页整张按比例缩放到「最长边 ≤ previewMax」输出（保持原图比例，不切分）。
// 每页预览都附带其对应真实图集页的尺寸（realW/realH），便于前端区分「预览尺寸」与「实际图集尺寸」。
static JsonObject RenderPreviewPages(AtlasBaker.AtlasBakeResult result, int previewMax)
{
    previewMax = Math.Clamp(previewMax, 64, 4096);
    var arr = new JsonArray();
    var realArr = new JsonArray();
    for (int pi = 0; pi < result.Pages.Count; pi++)
    {
        int realW = pi < result.AutoPages.Count ? result.AutoPages[pi].AtlasWidth : result.Pages[pi].Width;
        int realH = pi < result.AutoPages.Count ? result.AutoPages[pi].AtlasHeight : result.Pages[pi].Height;
        realArr.Add((JsonNode)new JsonObject { ["w"] = realW, ["h"] = realH });

        var page = result.Pages[pi];
        int w = page.Width, h = page.Height;
        using var srcBmp = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        page.RgbaPixels.CopyTo(srcBmp.GetPixelSpan());

        // 整张按比例缩放到最长边 ≤ previewMax
        int tw = w, th = h;
        int longest = Math.Max(w, h);
        if (longest > previewMax)
        {
            double scale = (double)previewMax / longest;
            tw = Math.Max(1, (int)Math.Round(w * scale));
            th = Math.Max(1, (int)Math.Round(h * scale));
        }
        // 预览画布 = 页面缩放尺寸 + 四周留白；页面外画棋盘格、页面内画深色底，
        // 两种背景色对比即可标出「图集页真实大小」边界（无需额外画边界线）
        const int pad = 16, cell = 8;
        int cw = tw + pad * 2, chh = th + pad * 2;
        using var scaled = new SKBitmap(cw, chh);
        using (var canvas = new SKCanvas(scaled))
        {
            // 页面外：棋盘格（与前端预览背景同款深色系）
            canvas.Clear(new SKColor(0x1b, 0x1f, 0x28));
            using (var dark = new SKPaint { Color = new SKColor(0x12, 0x14, 0x1a) })
            {
                for (int y = 0; y * cell < chh; y++)
                    for (int x = 0; x * cell < cw; x++)
                        if (((x + y) & 1) == 0)
                            canvas.DrawRect(new SKRect(x * cell, y * cell, (x + 1) * cell, (y + 1) * cell), dark);
            }
            // 页面实际区域：不透明深色底
            using var pageBg = new SKPaint { Color = new SKColor(0x23, 0x28, 0x33) };
            canvas.DrawRect(new SKRect(pad, pad, pad + tw, pad + th), pageBg);
            // 页面内容
            canvas.DrawBitmap(srcBmp,
                new SKRect(0, 0, w, h),
                new SKRect(pad, pad, pad + tw, pad + th),
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

// 写入服务器磁盘：{atlasName}_0.{png|webp} … + 描述 JSON（后缀可独立指定，空则按格式默认：PixiJS=.atlas.json，其余=.atlas.txt）
// PixiJS 多页：除主文件外，每页各写一个独立 Spritesheet JSON（文件名 = atlasName_i + suffix，与 related_multi_packs 一致）
static string WriteAtlasToDisk(AtlasBaker.AtlasBakeResult result, string outputFolder, string atlasName, AtlasFormat format, string suffix)
{
    if (!Directory.Exists(outputFolder)) Directory.CreateDirectory(outputFolder);
    const string ext = ".png";
    for (int i = 0; i < result.Pages.Count; i++)
    {
        var pngPath = Path.Combine(outputFolder, atlasName + "_" + i + ext);
        File.WriteAllBytes(pngPath, result.Pages[i].ToPng());
    }

    if (format == AtlasFormat.PixiJS)
    {
        var imageNames = new List<string>();
        for (int i = 0; i < result.AutoPages.Count; i++)
            imageNames.Add(atlasName + "_" + i + ext);
        var main = AtlasExporter.ToPixiJson(result.AutoPages, imageNames, atlasName, suffix);
        File.WriteAllText(Path.Combine(outputFolder, atlasName + suffix), main);
        for (int i = 1; i < result.AutoPages.Count; i++)
        {
            var pageJson = AtlasExporter.ToPixiJsonPage(result.AutoPages[i], imageNames[i], AtlasExporter.BuildAnimationsForPage(result.AutoPages, i));
            File.WriteAllText(Path.Combine(outputFolder, atlasName + "_" + i + suffix), pageJson);
        }
    }
    else
    {
        // 通用格式：直接写共享核心产出的 AtlasData JSON（已含全部页）
        File.WriteAllText(Path.Combine(outputFolder, atlasName + suffix), result.AtlasJson);
    }
    return Path.Combine(outputFolder, atlasName + suffix);
}

static AtlasFormat ParseFormat(string? s) => (s ?? "") switch
{
    "pixijs" or "pixi" => AtlasFormat.PixiJS,
    _ => AtlasFormat.Generic,
};

// 描述文件后缀：只接受两种（.atlas.txt / .atlas.json）；未指定或不合法则回退到该格式的默认后缀
static string ParseSuffix(string? s, AtlasFormat format) => (s ?? "") switch
{
    ".atlas.txt" or ".atlas.json" => s!,
    _ => AtlasConst.JsonExtension(format),
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
    // 输出目录留空时的默认：输入文件夹的同级目录 + 输入目录名 + ".Pack"
    // （注意：此时不让它参与图集名推导，否则图集名会变成 "xxx.Pack"）
    string? outGiven = string.IsNullOrWhiteSpace(outputFolder) ? null : outputFolder;
    var atlasName = ResolveAtlasName(ctx.Request.Query["atlasName"], outGiven, inputFolder);
    var bakeOptions = new AtlasBakeOptions { BaseName = atlasName };

    var (result, error) = BuildAtlasFromFolder(inputFolder ?? "", outGiven, settings, bakeOptions);
    if (error is not null)
        return Results.Text(error, "text/plain; charset=utf-8", statusCode: 400);

    SetMetaHeaders(ctx, result.AutoPages);
    ctx.Response.Headers["X-Atlas-Name"] = atlasName;
    ctx.Response.Headers["X-Output-Folder"] = outGiven ?? AtlasConst.DefaultPackOutputFolder(inputFolder ?? "");
    const int previewMax = 512; // 预览最大边长固定写死，不再由前端传参
    var json = RenderPreviewPages(result, previewMax);
    return Results.Text(json.ToJsonString(), "application/json; charset=utf-8");
});

// 本地模式：输入/输出文件夹路径 -> 写入服务器磁盘（GET，参数走 query）
app.MapGet("/api/pack", (HttpContext ctx, string? inputFolder, string? outputFolder, int? maxSize, int? padding, string? algorithm, bool? allowRotation, string? format, string? suffix) =>
{
    var settings = new PackerSettings
    {
        MaxSize = maxSize is > 0 ? maxSize.Value : 2048,
        Padding = padding is >= 0 ? padding.Value : 1,
        AllowRotation = allowRotation ?? false,
        Algorithm = ParseAlgorithm(algorithm),
    };
    var fmt = ParseFormat(format);
    var descSuffix = ParseSuffix(suffix, fmt);

    // 输出目录留空 → 输入文件夹的同级目录 + 输入目录名 + ".Pack"；
    // 此时不参与图集名推导，图集名仍取输入目录名（或用户指定值）。
    string? outGiven = string.IsNullOrWhiteSpace(outputFolder) ? null : outputFolder;
    string outDir = outGiven ?? AtlasConst.DefaultPackOutputFolder(inputFolder ?? "");
    var atlasName = ResolveAtlasName(ctx.Request.Query["atlasName"], outGiven, inputFolder);

    // 关键：BaseName 必须等于图集名，否则描述文件里的 image（atlas_0.png）
    // 会和实际写出的 PNG（Map_0.png）对不上，导致下游/反解找不到图集图片。
    var bakeOptions = new AtlasBakeOptions { BaseName = atlasName };

    var (result, error) = BuildAtlasFromFolder(inputFolder ?? "", outDir, settings, bakeOptions);
    if (error is not null)
        return Results.Text(error, "text/plain; charset=utf-8", statusCode: 400);

    var unplaced = result.AutoPages.Sum(p => p.Unplaced.Count);
    if (unplaced > 0)
        return Results.Text($"有 {unplaced} 张图片无法放入（可能单张超过最大边长），请调大 maxSize 或拆分。", "text/plain; charset=utf-8", statusCode: 400);

    var atlasPath = WriteAtlasToDisk(result, outDir, atlasName, fmt, descSuffix);
    return Results.Text("已生成图集：" + atlasPath + "（" + result.Pages.Count + " 页，前缀 " + atlasName + "）\n输出目录：" + outDir, "text/plain; charset=utf-8");
});

// ---------- 多文件夹模式：根目录下每个子目录 = 一个独立图集 ----------

// 枚举根目录下所有子目录，逐个构建图集。
// 每项 (Name, Result, Error)：Error 为 null 时 Result 有效，调用方负责处理。
static List<(string Name, AtlasBaker.AtlasBakeResult Result, string? Error)> BuildAllSubAtlases(
    string rootFolder, PackerSettings settings, string? outputFolder = null)
{
    var items = new List<(string, AtlasBaker.AtlasBakeResult, string?)>();
    foreach (var sub in Directory.EnumerateDirectories(rootFolder)
                 .OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
    {
        var name = SanitizeAtlasName(new DirectoryInfo(sub).Name);
        if (string.IsNullOrEmpty(name)) name = "atlas";
        // 每个子目录的图集名 = 目录名，必须同时作为 BaseName，
        // 保证描述文件里的 image 与写盘 PNG 名一致（否则会出现 "atlas_0.png" ↔ "Map_0.png" 对不上）
        var (result, error) = BuildAtlasFromFolder(sub, outputFolder, settings,
            new AtlasBakeOptions { BaseName = name });
        items.Add((name, result!, error));
    }
    return items;
}

// 多文件夹预览：返回 JSON { items:[{name,error?} | {name,pages,count,realPages}], okCount, failCount, totalPages, totalSprites, totalUnplaced }
app.MapGet("/api/multi-preview", (string? rootFolder, int? maxSize, int? padding, string? algorithm, bool? allowRotation) =>
{
    if (string.IsNullOrWhiteSpace(rootFolder) || !Directory.Exists(rootFolder))
        return Results.Text("根目录不存在：" + FullPathOf(rootFolder), "text/plain; charset=utf-8", statusCode: 400);

    var settings = new PackerSettings
    {
        MaxSize = maxSize is > 0 ? maxSize.Value : 2048,
        Padding = padding is >= 0 ? padding.Value : 1,
        AllowRotation = allowRotation ?? false,
        Algorithm = ParseAlgorithm(algorithm),
    };
    var items = new JsonArray();
    int okCount = 0, failCount = 0, totalSprites = 0, totalUnplaced = 0, totalPages = 0;
    const int previewMax = 512; // 与单文件夹模式一致：预览最长边固定
    foreach (var (name, result, error) in BuildAllSubAtlases(rootFolder, settings))
    {
        if (error is not null)
        {
            failCount++;
            items.Add((JsonNode)new JsonObject { ["name"] = name, ["error"] = error });
            continue;
        }
        okCount++;
        totalPages += result.Pages.Count;
        totalSprites += result.AutoPages.Sum(p => p.Sprites.Count);
        totalUnplaced += result.AutoPages.Sum(p => p.Unplaced.Count);
        var obj = RenderPreviewPages(result, previewMax);
        obj["name"] = name;
        obj["error"] = null;
        items.Add((JsonNode)obj);
    }

    var json = new JsonObject
    {
        ["items"] = items,
        ["okCount"] = okCount,
        ["failCount"] = failCount,
        ["totalPages"] = totalPages,
        ["totalSprites"] = totalSprites,
        ["totalUnplaced"] = totalUnplaced,
    };
    return Results.Text(json.ToJsonString(), "application/json; charset=utf-8");
});

// 多文件夹打包：把根目录下每个子图集目录分别打包并写入输出文件夹（每个图集前缀 = 子目录名）
app.MapGet("/api/multi-pack", (string? rootFolder, string? outputFolder, int? maxSize, int? padding, string? algorithm, bool? allowRotation, string? format, string? suffix) =>
{
    if (string.IsNullOrWhiteSpace(rootFolder) || !Directory.Exists(rootFolder))
        return Results.Text("根目录不存在：" + FullPathOf(rootFolder), "text/plain; charset=utf-8", statusCode: 400);

    var settings = new PackerSettings
    {
        MaxSize = maxSize is > 0 ? maxSize.Value : 2048,
        Padding = padding is >= 0 ? padding.Value : 1,
        AllowRotation = allowRotation ?? false,
        Algorithm = ParseAlgorithm(algorithm),
    };
    var fmt = ParseFormat(format);
    var descSuffix = ParseSuffix(suffix, fmt);

    // 输出目录留空 → 根目录的同级目录 + 根目录名 + ".Pack"（与单文件夹模式规则一致）
    string outDir = string.IsNullOrWhiteSpace(outputFolder)
        ? AtlasConst.DefaultPackOutputFolder(rootFolder)
        : outputFolder;

    int ok = 0, fail = 0;
    var sb = new System.Text.StringBuilder();
    foreach (var (name, result, error) in BuildAllSubAtlases(rootFolder, settings, outDir))
    {
        if (error is not null)
        {
            fail++;
            sb.AppendLine("✗ " + name + "：" + error);
            continue;
        }
        var unplaced = result.AutoPages.Sum(p => p.Unplaced.Count);
        if (unplaced > 0)
        {
            fail++;
            sb.AppendLine("✗ " + name + "：有 " + unplaced + " 张图片无法放入（可能单张超过最大边长），请调大 maxSize 或允许旋转。");
            continue;
        }
        var atlasPath = WriteAtlasToDisk(result, outDir, name, fmt, descSuffix);
        ok++;
        sb.AppendLine("✓ " + name + "：" + result.Pages.Count + " 页 → " + atlasPath);
    }

    var head = ok == 0 ? "全部失败：" : fail == 0 ? "全部完成：" : "完成（部分失败）：";
    var msg = head + "\n" + sb + "输出目录：" + outDir;
    return Results.Text(msg, "text/plain; charset=utf-8", statusCode: ok == 0 ? 400 : 200);
});

// ---------- 图集反解（单张图集 → 拆成一个个小图） ----------

// 扩展名过滤：filter 为逗号分隔的扩展名列表（可带点），命中任一即通过；
// 额外支持复合后缀场景（如 hero.atlas.txt —— 既算 .txt 也算 .atlas.txt）。
static bool IsAllowedExt(string file, string? filter)
{
    if (string.IsNullOrWhiteSpace(filter)) return true;
    var set = filter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(x => x.StartsWith('.') ? x : "." + x)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
    if (set.Count == 0) return true;

    string name = Path.GetFileName(file);
    if (set.Contains(Path.GetExtension(name))) return true;
    foreach (var ext in set)
        if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) return true;
    return false;
}

// 反解页专用的服务器端浏览：在目录列表基础上额外列出符合条件的文件（供选择图集图片 / 描述文件）。
app.MapGet("/api/files", (string? path, string? filter) =>
{
    try
    {
        string current, parent;
        List<string> dirs;
        List<string> files;

        if (string.IsNullOrWhiteSpace(path))
        {
            current = "";
            parent = "";
            dirs = System.IO.DriveInfo.GetDrives().Where(d => d.IsReady).Select(d => d.RootDirectory.FullName).ToList();
            files = new List<string>();
        }
        else
        {
            var dir = new DirectoryInfo(path);
            if (!dir.Exists) return Results.Text("目录不存在：" + FullPathOf(path), "text/plain; charset=utf-8", statusCode: 400);
            current = dir.FullName;
            parent = dir.Parent?.FullName ?? "";
            dirs = dir.EnumerateDirectories().OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).Select(d => d.FullName).ToList();
            files = dir.EnumerateFiles()
                .Where(f => IsAllowedExt(f.FullName, filter))
                .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .Select(f => f.FullName)
                .ToList();
        }

        var json = new JsonObject
        {
            ["current"] = current,
            ["parent"] = parent,
            ["dirs"] = new JsonArray(dirs.Select(d => (JsonNode)d).ToArray()),
            ["files"] = new JsonArray(files.Select(f => (JsonNode)f).ToArray()),
        };
        return Results.Text(json.ToJsonString(), "application/json; charset=utf-8");
    }
    catch (Exception ex)
    {
        return Results.Text("读取目录失败: " + ex.Message, "text/plain; charset=utf-8", statusCode: 500);
    }
});

// 把一张小图（PNG 字节）等比缩放到最长边 ≤ thumbMax 后返回 base64 PNG；本来就在限制内则直接返回原图字节。
static string MakeThumb(byte[] spritePng, int thumbMax)
{
    using var bmp = SKBitmap.Decode(spritePng);
    if (bmp is null) return "";

    int longest = Math.Max(bmp.Width, bmp.Height);
    if (longest <= thumbMax && longest > 0) return Convert.ToBase64String(spritePng);

    double scale = (double)thumbMax / longest;
    int tw = Math.Max(1, (int)Math.Round(bmp.Width * scale));
    int th = Math.Max(1, (int)Math.Round(bmp.Height * scale));

    using var scaled = new SKBitmap(tw, th, SKColorType.Rgba8888, SKAlphaType.Unpremul);
    using (var canvas = new SKCanvas(scaled))
    {
        canvas.Clear(SKColors.Transparent);
        canvas.DrawBitmap(bmp, new SKRect(0, 0, bmp.Width, bmp.Height), new SKRect(0, 0, tw, th),
            new SKSamplingOptions(SKFilterMode.Linear));
    }
    using var img = SKImage.FromBitmap(scaled);
    using var data = img.Encode(SKEncodedImageFormat.Png, 100);
    return Convert.ToBase64String(data.ToArray());
}

// 扫描一个文件夹：找出其中所有「图集」（自动识别通用格式 / PixiJS 描述文件），逐个拆图并返回缩略图（不写盘）。
// 非图集文件一律忽略。
app.MapGet("/api/unpack-preview", (HttpContext ctx, string? inputFolder, string? outputFolder, int? limit) =>
{
    if (string.IsNullOrWhiteSpace(inputFolder) || !Directory.Exists(inputFolder))
        return Results.Text("输入文件夹不存在：" + FullPathOf(inputFolder), "text/plain; charset=utf-8", statusCode: 400);

    try
    {
        string outDir = string.IsNullOrWhiteSpace(outputFolder)
            ? AtlasFolderUnpacker.DefaultOutputFolder(inputFolder)
            : outputFolder;

        var warnings = new List<string>();
        var sources = AtlasFolderUnpacker.Discover(inputFolder, true, warnings);
        const int thumbMax = 128;                        // 缩略图最长边固定，控制响应体大小
        int budget = Math.Clamp(limit ?? 200, 1, 5000);  // 全部图集合计最多返回多少张缩略图

        var atlasArr = new JsonArray();
        var failures = new JsonArray();
        int totalSprites = 0, totalSkipped = 0, totalShown = 0;

        foreach (var src in sources)
        {
            try
            {
                var result = AtlasUnpacker.Unpack(src.AtlasImage, src.Description);
                totalSprites += result.Sprites.Count;
                totalSkipped += result.Skipped;

                var arr = new JsonArray();
                int take = Math.Min(result.Sprites.Count, budget);
                for (int i = 0; i < take; i++)
                {
                    var s = result.Sprites[i];
                    arr.Add((JsonNode)new JsonObject
                    {
                        ["name"] = s.Name,
                        ["file"] = s.FileName,
                        ["w"] = s.Width,
                        ["h"] = s.Height,
                        ["x"] = s.SourceX,
                        ["y"] = s.SourceY,
                        ["rotated"] = s.WasRotated,
                        ["png"] = MakeThumb(s.Bytes, thumbMax),
                    });
                }
                budget -= take;
                totalShown += take;

                atlasArr.Add((JsonNode)new JsonObject
                {
                    ["key"] = src.Key,
                    ["image"] = Path.GetFileName(src.AtlasImage),
                    ["imagePath"] = src.AtlasImage,
                    ["desc"] = Path.GetFileName(src.Description),
                    ["pageIndex"] = src.PageIndex,
                    ["page"] = new JsonObject { ["w"] = src.PageWidth, ["h"] = src.PageHeight },
                    ["fromPixiJs"] = src.FromPixiJs,
                    ["total"] = result.Sprites.Count,
                    ["shown"] = take,
                    ["skipped"] = result.Skipped,
                    ["sprites"] = arr,
                });
            }
            catch (Exception ex)
            {
                failures.Add((JsonNode)(Path.GetFileName(src.AtlasImage) + "：" + ex.Message));
            }
        }

        var json = new JsonObject
        {
            ["inputFolder"] = inputFolder,
            ["outputFolder"] = outDir,
            ["atlasCount"] = sources.Count,
            ["totalSprites"] = totalSprites,
            ["totalShown"] = totalShown,
            ["totalSkipped"] = totalSkipped,
            ["atlases"] = atlasArr,
            ["failures"] = failures,
            ["warnings"] = new JsonArray(warnings.Select(w => (JsonNode)w).ToArray()),
        };

        ctx.Response.Headers["X-Atlas-Count"] = sources.Count.ToString();
        ctx.Response.Headers["X-Sprite-Total"] = totalSprites.ToString();
        ctx.Response.Headers["X-Skip-Count"] = totalSkipped.ToString();
        return Results.Text(json.ToJsonString(), "application/json; charset=utf-8");
    }
    catch (Exception ex)
    {
        return Results.Text("扫描/反解失败: " + ex.Message, "text/plain; charset=utf-8", statusCode: 400);
    }
});

// 反解并写入磁盘：遍历输入文件夹，把所有图集拆成小图，输出到输出文件夹（多个图集各自一个子目录）。
app.MapGet("/api/unpack", (string? inputFolder, string? outputFolder, string? format) =>
{
    if (string.IsNullOrWhiteSpace(inputFolder) || !Directory.Exists(inputFolder))
        return Results.Text("输入文件夹不存在：" + FullPathOf(inputFolder), "text/plain; charset=utf-8", statusCode: 400);

    var fmt = string.Equals(format, "webp", StringComparison.OrdinalIgnoreCase)
        ? AtlasUnpacker.OutputFormat.Webp
        : AtlasUnpacker.OutputFormat.Png;

    try
    {
        var result = AtlasFolderUnpacker.UnpackFolder(inputFolder, outputFolder, fmt);
        string ext = AtlasUnpacker.ExtOf(fmt);

        if (result.Atlases.Count == 0 && result.Failures.Count == 0)
        {
            var msg = "在输入文件夹里没有发现任何图集（需要「图集图片 + 描述文件」成对存在）。\n文件夹：" + FullPathOf(inputFolder);
            foreach (var w in result.Warnings) msg += "\n  ⚠ " + w;
            return Results.Text(msg, "text/plain; charset=utf-8", statusCode: 400);
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("✓ 共识别出 " + result.Atlases.Count + " 个图集，拆出 " + result.SpriteCount + " 张小图（" + ext + "）");
        sb.AppendLine("输出目录：" + result.OutputFolder);
        foreach (var a in result.Atlases)
            sb.AppendLine("  ✓ " + a.Key + "：" + a.Sprites.Count + " 张 → " + a.OutputFolder);
        foreach (var f in result.Failures)
            sb.AppendLine("  ✗ " + f);
        foreach (var w in result.Warnings)
            sb.AppendLine("  ⚠ " + w);
        if (result.SkippedCount > 0)
            sb.AppendLine("⚠ 有 " + result.SkippedCount + " 个区域越界或尺寸非法，已跳过。");

        return Results.Text(sb.ToString().TrimEnd('\r', '\n'), "text/plain; charset=utf-8");
    }
    catch (Exception ex)
    {
        return Results.Text("反解失败: " + ex.Message, "text/plain; charset=utf-8", statusCode: 400);
    }
});

app.Run();
