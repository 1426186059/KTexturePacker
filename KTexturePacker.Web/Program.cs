using System.IO.Compression;
using KTexturePacker.Core;
using SkiaSharp;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

// 把上传的多张图片打包成图集，返回 zip（atlas.png + 描述文件）。
// 请求为 multipart/form-data：files（图片）+ 参数（maxSize/padding/allowRotation/algorithm/format）。
// 全程不依赖 JSON 反射反序列化，原生 AOT 下安全。
app.MapPost("/api/pack", async (HttpContext ctx) =>
{
    IFormCollection form;
    try
    {
        form = await ctx.Request.ReadFormAsync();
    }
    catch (Exception ex)
    {
        return Results.Text("读取表单失败: " + ex.Message, "text/plain; charset=utf-8", statusCode: 400);
    }

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

    var inputs = new List<SpriteInput>();
    foreach (var file in form.Files)
    {
        if (file.Length == 0)
            continue;

        var ms = new MemoryStream();
        await file.CopyToAsync(ms);
        var bmp = SKBitmap.Decode(ms.ToArray());
        if (bmp is null)
            continue;

        var name = Path.GetFileNameWithoutExtension(file.FileName);
        if (string.IsNullOrWhiteSpace(name))
            name = "sprite_" + inputs.Count;
        inputs.Add(new SpriteInput(name, bmp));
    }

    if (inputs.Count == 0)
        return Results.Text($"没有提供有效的图片（收到 {form.Files.Count} 个文件）。", "text/plain; charset=utf-8", statusCode: 400);

    var result = AtlasPacker.Pack(inputs, settings);
    var atlas = AtlasPacker.RenderAtlas(result);
    using var atlasData = atlas.Encode(SKEncodedImageFormat.Png, 100);
    if (atlasData is null)
    {
        foreach (var s in inputs)
            s.Bitmap.Dispose();
        atlas.Dispose();
        return Results.StatusCode(StatusCodes.Status500InternalServerError);
    }

    var imageName = "atlas.png";
    var descName = format == ExportFormat.LibGdx ? "atlas.atlas" : "atlas.json";
    var desc = format == ExportFormat.LibGdx
        ? AtlasExporter.ToLibGdx(result, imageName)
        : AtlasExporter.ToJson(result, imageName);

    var zipMs = new MemoryStream();
    using (var zip = new ZipArchive(zipMs, ZipArchiveMode.Create, true))
    {
        var eImg = zip.CreateEntry(imageName);
        using (var zs = eImg.Open())
        {
            atlasData.AsStream().CopyTo(zs);
        }

        var eDesc = zip.CreateEntry(descName);
        using (var zs = eDesc.Open())
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(desc);
            zs.Write(bytes, 0, bytes.Length);
        }
    }

    zipMs.Position = 0;

    foreach (var s in inputs)
        s.Bitmap.Dispose();
    atlas.Dispose();

    return Results.File(zipMs, "application/zip", "atlas.zip");
});

static MaxRectsMethod ParseAlgorithm(string? s) => s switch
{
    "long" => MaxRectsMethod.BestLongSideFit,
    "bottomleft" => MaxRectsMethod.BottomLeftRule,
    "contact" => MaxRectsMethod.ContactPointRule,
    _ => MaxRectsMethod.BestShortSideFit,
};

app.Run();
