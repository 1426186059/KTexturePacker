namespace KTexturePacker.Core
{
    public static class AtlasConst
    {
        public static string JsonExtension(AtlasFormat format)
        {
            switch (format)
            {
                case AtlasFormat.PixiJS:
                    return ".atlas.json";
                default:
                    return ".atlas.txt";
            }
        }

        /// <summary>打包默认输出目录的后缀：&lt;输入目录名&gt;.Pack</summary>
        public const string PackOutputSuffix = ".Pack";

        /// <summary>反解默认输出目录的后缀：&lt;输入目录名&gt;.UnPack</summary>
        public const string UnpackOutputSuffix = ".UnPack";

        /// <summary>
        /// 打包的默认输出目录：输入文件夹的<b>同级目录</b>，名字 = 输入目录名 + <see cref="PackOutputSuffix"/>。
        /// 例：D:\art\hero → D:\art\hero.Pack
        /// </summary>
        public static string DefaultPackOutputFolder(string inputFolder) => SiblingFolderWithSuffix(inputFolder, PackOutputSuffix);

        /// <summary>
        /// 反解的默认输出目录：输入文件夹的<b>同级目录</b>，名字 = 输入目录名 + <see cref="UnpackOutputSuffix"/>。
        /// 例：D:\out\hero → D:\out\hero.UnPack
        /// </summary>
        public static string DefaultUnpackOutputFolder(string inputFolder) => SiblingFolderWithSuffix(inputFolder, UnpackOutputSuffix);

        /// <summary>取输入目录的同级目录，目录名 = 输入目录名 + 后缀。</summary>
        public static string SiblingFolderWithSuffix(string inputFolder, string suffix)
        {
            string full = Path.GetFullPath((inputFolder ?? "").Trim()
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var di = new DirectoryInfo(full);

            string name = di.Name;
            // 盘根目录（如 D:\）没有目录名，退回盘符
            if (string.IsNullOrEmpty(name)) name = full.Replace(":", "").Trim(Path.DirectorySeparatorChar);
            if (string.IsNullOrEmpty(name)) name = "atlas";

            string parent = di.Parent?.FullName ?? "";
            if (string.IsNullOrEmpty(parent)) parent = full;

            return Path.Combine(parent, name + suffix);
        }
    }
}
