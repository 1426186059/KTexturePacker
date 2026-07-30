using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using KTexturePacker.Core;
using SkiaSharp;
using SkiaSharp.Views.Maui;

namespace KTexturePacker.Pages;

/// <summary>
/// 列表中展示的一张精灵（含缩略图）。
/// </summary>
public sealed class SpriteItem
{
    public string Name { get; }
    public SKBitmap Bitmap { get; }
    public ImageSource Thumbnail { get; }
    public int Width => Bitmap.Width;
    public int Height => Bitmap.Height;
    public string SizeText => $"{Width} x {Height} px";

    public SpriteItem(string name, SKBitmap bitmap)
    {
        Name = name;
        Bitmap = bitmap;
        Thumbnail = MakeThumbnail(bitmap);
    }

    private static ImageSource MakeThumbnail(SKBitmap bmp)
    {
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        var ms = new MemoryStream();
        data.AsStream().CopyTo(ms);
        ms.Position = 0;
        return ImageSource.FromStream(() => ms);
    }
}

public partial class PackerPage : ContentPage
{
    private readonly ObservableCollection<SpriteItem> _sprites = new();
    private SKBitmap? _atlasBitmap;
    private PackingResult? _lastResult;

    public PackerPage()
    {
        InitializeComponent();
        SpriteList.ItemsSource = _sprites;
    }

    private async void OnAddImagesClicked(object? sender, EventArgs e)
    {
        try
        {
            var result = await FilePicker.PickMultipleAsync(new PickOptions
            {
                PickerTitle = "Select sprite images",
                FileTypes = FilePickerFileType.Images,
            });
            if (result is null)
                return;

            foreach (var file in result)
                await AddFromStream(file.FileName, await file.OpenReadAsync());

            UpdateStatus($"{_sprites.Count} sprites loaded.");
        }
        catch (Exception ex)
        {
            await AppShell.DisplayToastAsync("Add images failed: " + ex.Message);
        }
    }

    private async void OnAddFolderClicked(object? sender, EventArgs e)
    {
        try
        {
            var folder = await global::CommunityToolkit.Maui.Storage.FolderPicker.PickAsync("Select a folder of sprites");
            var fsFolder = folder?.Folder;
            if (fsFolder is null)
                return;

            var dir = fsFolder.Path;
            if (!Directory.Exists(dir))
                return;

            var exts = new HashSet<string> { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp" };
            foreach (var path in Directory.EnumerateFiles(dir, "*.*", SearchOption.TopDirectoryOnly))
            {
                var ext = Path.GetExtension(path).ToLowerInvariant();
                if (!exts.Contains(ext))
                    continue;
                await AddFromStream(Path.GetFileName(path), File.OpenRead(path));
            }

            UpdateStatus($"{_sprites.Count} sprites loaded.");
        }
        catch (Exception ex)
        {
            await AppShell.DisplayToastAsync("Add folder failed: " + ex.Message);
        }
    }

    private async Task AddFromStream(string name, Stream stream)
    {
        using var s = stream;
        var bmp = SKBitmap.Decode(s);
        if (bmp is null)
            return;
        _sprites.Add(new SpriteItem(name, bmp));
    }

    private void OnRemoveClicked(object? sender, EventArgs e)
    {
        if (sender is Button { CommandParameter: SpriteItem item })
        {
            _sprites.Remove(item);
            item.Bitmap.Dispose();
        }
    }

    private void OnClearClicked(object? sender, EventArgs e)
    {
        foreach (var s in _sprites)
            s.Bitmap.Dispose();
        _sprites.Clear();

        _atlasBitmap?.Dispose();
        _atlasBitmap = null;
        _lastResult = null;
        AtlasCanvas.InvalidateSurface();
        UpdateStatus("Cleared.");
    }

    private void OnPackClicked(object? sender, EventArgs e)
    {
        if (_sprites.Count == 0)
        {
            UpdateStatus("No sprites to pack.");
            return;
        }

        var settings = BuildSettings();
        var inputs = _sprites.Select(s => new SpriteInput(s.Name, s.Bitmap)).ToList();
        var result = AtlasPacker.Pack(inputs, settings);

        _atlasBitmap?.Dispose();
        _atlasBitmap = AtlasPacker.RenderAtlas(result);
        _lastResult = result;
        AtlasCanvas.InvalidateSurface();

        long used = result.Sprites.Sum(p => (long)p.Width * p.Height);
        double eff = result.AtlasWidth * result.AtlasHeight > 0
            ? used * 100.0 / (result.AtlasWidth * result.AtlasHeight)
            : 0;
        UpdateStatus($"Atlas {result.AtlasWidth}x{result.AtlasHeight} | placed {result.Sprites.Count} | " +
                     $"unplaced {result.Unplaced.Count} | fill {eff:F1}%");
    }

    private async void OnExportClicked(object? sender, EventArgs e)
    {
        if (_atlasBitmap is null || _lastResult is null)
        {
            await AppShell.DisplayToastAsync("Pack first.");
            return;
        }

        try
        {
            var format = FormatPicker.SelectedIndex == 1 ? ExportFormat.LibGdx : ExportFormat.Json;

            using var img = SKImage.FromBitmap(_atlasBitmap);
            using var data = img.Encode(SKEncodedImageFormat.Png, 100);
            using var ms = new MemoryStream();
            data.AsStream().CopyTo(ms);
            ms.Position = 0;

            var pngResult = await global::CommunityToolkit.Maui.Storage.FileSaver.Default.SaveAsync("atlas.png", ms);
            if (!pngResult.IsSuccessful)
            {
                await AppShell.DisplayToastAsync("Export cancelled.");
                return;
            }

            string dir = Path.GetDirectoryName(pngResult.FilePath) ?? ".";
            string baseName = Path.GetFileNameWithoutExtension(pngResult.FilePath);
            string imageName = Path.GetFileName(pngResult.FilePath);
            string ext = format == ExportFormat.LibGdx ? ".atlas" : ".json";
            string dataPath = Path.Combine(dir, baseName + ext);
            string content = format == ExportFormat.LibGdx
                ? AtlasExporter.ToLibGdx(_lastResult, imageName)
                : AtlasExporter.ToJson(_lastResult, imageName);

            await File.WriteAllTextAsync(dataPath, content);
            await AppShell.DisplayToastAsync($"Saved {imageName} + {Path.GetFileName(dataPath)}");
        }
        catch (Exception ex)
        {
            await AppShell.DisplayToastAsync("Export failed: " + ex.Message);
        }
    }

    private PackerSettings BuildSettings()
    {
        int maxSize = int.TryParse(MaxSizePicker.SelectedItem?.ToString(), out var m) ? m : 2048;
        int pad = int.TryParse(PaddingEntry.Text, out var p) ? Math.Max(0, p) : 1;
        var algo = MaxRectsMethod.BestShortSideFit;
        return new PackerSettings
        {
            MaxSize = maxSize,
            Padding = pad,
            AllowRotation = AllowRotationSwitch.IsToggled,
            Algorithm = algo,
        };
    }

    private void AtlasCanvas_PaintSurface(object? sender, SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        canvas.Clear(new SKColor(0xFF1B1B1F));
        if (_atlasBitmap is null)
            return;

        float scale = Math.Min(e.Info.Width / (float)_atlasBitmap.Width, e.Info.Height / (float)_atlasBitmap.Height);
        if (scale <= 0)
            return;

        float dw = _atlasBitmap.Width * scale;
        float dh = _atlasBitmap.Height * scale;
        float dx = (e.Info.Width - dw) / 2;
        float dy = (e.Info.Height - dh) / 2;
        canvas.DrawBitmap(_atlasBitmap, new SKRect(dx, dy, dx + dw, dy + dh), new SKSamplingOptions(SKFilterMode.Linear));
    }

    private void UpdateStatus(string text) => StatusLabel.Text = text;
}
