using KTexturePacker.Core.JsonFormat;

namespace KTexturePacker.Core;

/// <summary>
/// 在文件夹里被识别出来的一个「图集」= 一张图集图片 + 它的描述文件（对应描述文件中的某一页）。
/// </summary>
public sealed class AtlasSource
{
    /// <summary>图集图片文件路径（如 atlas_0.png）。</summary>
    public string AtlasImage { get; init; } = "";

    /// <summary>描述文件路径（如 atlas.atlas.txt）。</summary>
    public string Description { get; init; } = "";

    /// <summary>该图集在描述文件中的页序号。</summary>
    public int PageIndex { get; init; }

    /// <summary>批次输出时用的标识名（默认取描述文件名，已去重）。</summary>
    public string Key { get; set; } = "";

    /// <summary>描述中该页声明的尺寸（px）。</summary>
    public int PageWidth { get; init; }
    public int PageHeight { get; init; }

    /// <summary>该页声明的子图数量。</summary>
    public int RegionCount { get; init; }

    /// <summary>描述文件是否为 PixiJS 格式。</summary>
    public bool FromPixiJs { get; init; }
}

/// <summary>
/// 按文件夹批量反解的结果。
/// </summary>
public sealed class FolderUnpackResult
{
    /// <summary>输入的根文件夹。</summary>
    public string InputFolder { get; set; } = "";

    /// <summary>最终使用的输出文件夹。</summary>
    public string OutputFolder { get; set; } = "";

    /// <summary>成功反解的每个图集。</summary>
    public List<AtlasUnpackResult> Atlases { get; } = new();

    /// <summary>识别失败 / 反解失败的图集及其原因。</summary>
    public List<string> Failures { get; } = new();

    /// <summary>被跳过的可疑描述文件及原因（如描述里声明的图集图片找不到）。</summary>
    public List<string> Warnings { get; } = new();

    /// <summary>本次共拆出的小图数量。</summary>
    public int SpriteCount => Atlases.Sum(a => a.Sprites.Count);

    /// <summary>本次被跳过的非法区域数量。</summary>
    public int SkippedCount => Atlases.Sum(a => a.Skipped);
}

/// <summary>
/// 文件夹级图集反解器：遍历一个文件夹，把「凡是图集的」都拆出来，不是图集的文件一律忽略。
/// <para>
/// 识别规则：逐个尝试候选描述文件（<c>*.json</c> / <c>*.txt</c> / <c>*.atlas*</c>）→
/// 能按通用格式或 PixiJS v8 格式解析出 pages / frames，且其 <c>image</c> 指向的图片文件真实存在，
/// 才算一个图集（多页描述文件会拆出多个图集）。没有任何图集信息的普通图片、文本文件等直接跳过。
/// </para>
/// 输出根目录默认是「输入文件夹的同级目录 + <c>.Atlas.UnPack</c>」（见 <see cref="DefaultOutputFolder"/>）。
/// </summary>
public static class AtlasFolderUnpacker
{
    /// <summary>输出目录后缀（= <see cref="AtlasConst.UnpackOutputSuffix"/>）。</summary>
    public const string OutputSuffix = AtlasConst.UnpackOutputSuffix;

    /// <summary>
    /// 默认输出目录：输入文件夹的<b>同级目录</b>，名字为「输入目录名 + .UnPack」。
    /// 例：输入 <c>D:\out\hero</c> → 输出 <c>D:\out\hero.UnPack</c>。
    /// </summary>
    public static string DefaultOutputFolder(string inputFolder) => AtlasConst.DefaultUnpackOutputFolder(inputFolder);

    /// <summary>
    /// 遍历文件夹，找出其中所有「图集」。<paramref name="recursive"/> 为 true 时一并遍历子目录。
    /// 找不到的可疑描述文件（例如声明的图片不存在）会写入 <paramref name="warnings"/>。
    /// </summary>
    public static List<AtlasSource> Discover(string inputFolder, bool recursive = true, List<string>? warnings = null)
    {
        if (!Directory.Exists(inputFolder))
            throw new DirectoryNotFoundException("输入文件夹不存在: " + inputFolder);

        var files = Directory.EnumerateFiles(inputFolder, "*",
            recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly).ToList();

        var sources = new List<AtlasSource>();
        var seenImages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var usedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var desc in files.Where(IsDescriptionCandidate).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            AtlasData data;
            bool pixi;
            try
            {
                data = AtlasUnpacker.LoadDescription(File.ReadAllText(desc), out pixi);
            }
            catch
            {
                continue; // 不是图集描述文件 → 忽略
            }

            if (data.Pages.Count == 0) continue;

            for (int i = 0; i < data.Pages.Count; i++)
            {
                var page = data.Pages[i];
                string? img = ResolveImageFile(page.Image, desc, files, warnings);
                if (img is null) continue;              // 描述里声明的图片不在 → 忽略这一页
                if (!seenImages.Add(img)) continue;      // 同一张图集不重复处理

                // 多页描述文件会拆出多个图集，逐个分配唯一的输出子目录名（npc / npc_1 …）
                string key = UniqueKey(KeyOfDescription(desc), usedKeys);

                sources.Add(new AtlasSource
                {
                    AtlasImage = img,
                    Description = desc,
                    PageIndex = i,
                    Key = key,
                    PageWidth = page.Width,
                    PageHeight = page.Height,
                    RegionCount = page.Regions.Count,
                    FromPixiJs = pixi,
                });
            }
        }

        return sources;
    }

    /// <summary>
    /// 反解一个文件夹内的所有图集并写入磁盘。
    /// 输出目录留空时用 <see cref="DefaultOutputFolder"/>；
    /// 只有<b>一个</b>图集时小图直接放在输出目录，有多个图集时按 Key 各建一个子目录，避免重名互相覆盖。
    /// </summary>
    public static FolderUnpackResult UnpackFolder(
        string inputFolder,
        string? outputFolder = null,
        AtlasUnpacker.OutputFormat format = AtlasUnpacker.OutputFormat.Png,
        bool recursive = true)
    {
        if (!Directory.Exists(inputFolder))
            throw new DirectoryNotFoundException("输入文件夹不存在: " + inputFolder);

        string outDir = string.IsNullOrWhiteSpace(outputFolder) ? DefaultOutputFolder(inputFolder) : outputFolder;
        var warnings = new List<string>();
        var sources = Discover(inputFolder, recursive, warnings);

        var result = new FolderUnpackResult { InputFolder = inputFolder, OutputFolder = outDir };
        if (sources.Count == 0) return result;

        result.Warnings.AddRange(warnings);
        bool multi = sources.Count > 1;
        if (!Directory.Exists(outDir)) Directory.CreateDirectory(outDir);
        foreach (var src in sources)
        {
            try
            {
                var one = AtlasUnpacker.Unpack(src.AtlasImage, src.Description, format);
                one.Key = src.Key;
                string target = multi ? Path.Combine(outDir, src.Key) : outDir;
                if (!Directory.Exists(target)) Directory.CreateDirectory(target);

                AtlasUnpacker.WriteSprites(one.Sprites, target, format);
                one.OutputFolder = target;
                result.Atlases.Add(one);
            }
            catch (Exception ex)
            {
                result.Failures.Add(Path.GetFileName(src.AtlasImage) + "：" + ex.Message);
            }
        }

        return result;
    }

    // ==================================================================
    //  内部辅助
    // ==================================================================

    private static bool IsDescriptionCandidate(string file)
    {
        var ext = Path.GetExtension(file).ToLowerInvariant();
        return ext is ".json" or ".txt" or ".atlas" || file.EndsWith(".atlas.txt", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>图集图片可能的扩展名（描述里写的后缀与实际文件不一致时依次尝试）。</summary>
    private static readonly string[] ImageExtensions = { ".png", ".webp", ".jpg", ".jpeg", ".bmp", ".gif", ".tga" };

    /// <summary>
    /// 把描述里声明的 image（可能带相对目录）定位到磁盘上的真实文件。
    /// 找不到时返回 null 并写入一条 warning —— 绝不跨目录乱配图片，避免拆出错误内容。
    /// </summary>
    private static string? ResolveImageFile(string? pageImage, string descriptionFile, List<string> allFiles, List<string>? warnings)
    {
        if (string.IsNullOrWhiteSpace(pageImage)) return null;

        string rel = pageImage.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        string descDir = Path.GetDirectoryName(Path.GetFullPath(descriptionFile)) ?? "";

        // 1) 绝对路径
        if (Path.IsPathRooted(rel) && File.Exists(rel)) return rel;

        // 2) 描述文件同目录下的同名文件（大小写不敏感）
        var inDir = allFiles.Where(f => string.Equals(Path.GetDirectoryName(Path.GetFullPath(f)), descDir, StringComparison.OrdinalIgnoreCase));
        var hit = inDir.FirstOrDefault(f => string.Equals(Path.GetFileName(f), Path.GetFileName(rel), StringComparison.OrdinalIgnoreCase));
        if (hit is not null) return hit;

        // 3) image 带了相对子目录
        string withRel = Path.GetFullPath(Path.Combine(descDir, rel.TrimStart(Path.DirectorySeparatorChar)));
        if (File.Exists(withRel)) return withRel;

        // 4) 描述文件名与图集图片名不一致时的兜底：
        //    例 Map.atlas.txt 里写 "image":"atlas_0.png"，而实际文件叫 Map_0.png
        //    → 用「描述文件名 + 图片名的数字后缀」在描述所在目录匹配
        string declaredExt = Path.GetExtension(rel);
        string declaredStem = Path.GetFileNameWithoutExtension(rel);
        int d = declaredStem.Length;
        while (d > 0 && declaredStem[d - 1] >= '0' && declaredStem[d - 1] <= '9') d--;
        string digits = d < declaredStem.Length ? declaredStem[d..] : "";   // "0" / "12" / ""
        string descStem = KeyOfDescription(descriptionFile);

        var candidates = new List<string>();
        if (digits.Length > 0) candidates.Add(descStem + "_" + digits);
        candidates.Add(descStem + digits);
        candidates.Add(descStem);

        foreach (var stem in candidates)
        {
            foreach (var ext in new[] { declaredExt }.Concat(ImageExtensions))
            {
                if (string.IsNullOrEmpty(ext)) continue;
                string p = Path.Combine(descDir, stem + ext);
                if (!File.Exists(p)) continue;
                warnings?.Add($"“{Path.GetFileName(descriptionFile)}” 声明的图片是 “{pageImage}”，实际按 “{Path.GetFileName(p)}” 匹配（两者名字不一致）。");
                return p;
            }
        }

        warnings?.Add($"“{Path.GetFileName(descriptionFile)}”：描述里声明的图集图片 “{pageImage}” 找不到，已忽略。");
        return null;
    }

    /// <summary>描述文件名 → 输出子目录名（去掉 .atlas / .json / .txt 等所有扩展名）。</summary>
    private static string KeyOfDescription(string descriptionFile)
    {
        string name = Path.GetFileNameWithoutExtension(descriptionFile); // 去 .txt/.json
        // 再去掉可能存在的第二段扩展名（如 hero.atlas.txt → hero）
        while (true)
        {
            var ext = Path.GetExtension(name);
            if (string.IsNullOrEmpty(ext)) break;
            name = name[..^ext.Length];
        }
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (char c in name.Trim())
            sb.Append(c is '\\' or '/' or ':' or '*' or '?' or '"' or '<' or '>' or '|' ? '_' : c);
        string s = sb.ToString().Trim('_', '.', ' ');
        return string.IsNullOrEmpty(s) ? "atlas" : s;
    }

    private static string UniqueKey(string key, HashSet<string> used)
    {
        string name = key;
        int k = 1;
        while (!used.Add(name))
            name = key + "_" + (k++);
        return name;
    }
}
