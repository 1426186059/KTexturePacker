using SkiaSharp;
using KTexturePacker.Core;

var outDir = Path.Combine(AppContext.BaseDirectory, "smoke");
Directory.CreateDirectory(outDir);

// 生成 6 张 512x512 + 3 张 128x128
var colors = new[] { SKColors.Red, SKColors.Green, SKColors.Blue, SKColors.Yellow, SKColors.Cyan, SKColors.Magenta };
for (int i = 0; i < colors.Length; i++)
{
    using var bmp = new SKBitmap(512, 512);
    using var canvas = new SKCanvas(bmp);
    canvas.Clear(colors[i]);
    using var data = bmp.Encode(SKEncodedImageFormat.Png, 100);
    File.WriteAllBytes(Path.Combine(outDir, "img" + i + ".png"), data.ToArray());
}
for (int i = 0; i < 3; i++)
{
    using var bmp = new SKBitmap(128, 128);
    using var canvas = new SKCanvas(bmp);
    canvas.Clear(new SKColor((byte)(i * 80), 30, 200));
    using var data = bmp.Encode(SKEncodedImageFormat.Png, 100);
    File.WriteAllBytes(Path.Combine(outDir, "small" + i + ".png"), data.ToArray());
}

// 直接调试 PackPages
var inputs = new List<SpriteInput>();
foreach (var f in Directory.EnumerateFiles(outDir).Where(x => x.EndsWith(".png")))
{
    var bmp = SKBitmap.Decode(f)!;
    inputs.Add(new SpriteInput(Path.GetFileNameWithoutExtension(f), bmp));
}
Console.WriteLine($"inputs.Count = {inputs.Count}");

void Test(int maxSize, bool allowRot)
{
    var settings = new PackerSettings { MaxSize = maxSize, Padding = 1, AllowRotation = allowRot, Algorithm = MaxRectsMethod.BestShortSideFit };
    var pages = AtlasPacker.PackPages(inputs, settings);
    int totalPlaced = pages.Sum(p => p.Sprites.Count);
    int totalUnplaced = pages.Sum(p => p.Unplaced.Count);
    Console.WriteLine($"maxSize={maxSize} allowRot={allowRot} => pages={pages.Count} placed={totalPlaced} unplaced={totalUnplaced}");
    for (int i = 0; i < pages.Count; i++)
        Console.WriteLine($"   page{i}: {pages[i].AtlasWidth}x{pages[i].AtlasHeight} sprites={pages[i].Sprites.Count} unplaced={pages[i].Unplaced.Count}");
}

Test(2048, false);
Test(1024, false);
Test(1024, true);
Test(512, false);
