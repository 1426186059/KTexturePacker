using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.U2D.Sprites;
using UnityEngine;

using KTexturePacker.Parser;

namespace KTexturePacker.Unity.Editor
{
    /// <summary>
    /// 选中 KTexturePacker 导出的图集文件（*.atlas.txt，JSON 格式）后，
    /// 通过菜单把同目录下对应的纹理 PNG 设为 Sprite(2D and UI) 的 Multiple 模式，
    /// 并按图集 JSON 中的 regions 写入每个子图的 SpriteMetaData（Sprite 切片）。
    ///
    /// 本工具只修改 PNG 的导入设置（.meta），不读取/不重写磁盘上的 PNG 像素，
    /// 因此对纹理本身零侵入。旋转子图（region.Rotated == true）按其在纹理中的
    /// 实际占位矩形（x,y,w,h）直接切片——纹理内是否旋转由打包策略决定，本工具不处理。
    ///
    /// 坐标约定：KTexturePacker 的像素坐标系原点在纹理左上、Y 轴向下，与 Unity
    /// TextureImporter 的 spriteSheet.rect 坐标系一致，故 rect 直接填入 x/y/w/h 即可。
    /// </summary>
    public static class KTexturePackerAtlasImporter
    {
        private const string MenuPath = "Assets/KTexturePacker/Generate Atlas Sprite";

        [MenuItem(MenuPath, true)]
        private static bool GenerateAtlasSpriteValidate()
        {
            return GetSelectedAtlasTextAsset() != null;
        }

        [MenuItem(MenuPath, false, 1000)]
        private static void GenerateAtlasSprite()
        {
            TextAsset atlasAsset = GetSelectedAtlasTextAsset();
            if (atlasAsset == null) return;

            string atlasPath = AssetDatabase.GetAssetPath(atlasAsset);
            string atlasDir = Path.GetDirectoryName(atlasPath);

            string json = atlasAsset.text;
            if (string.IsNullOrEmpty(json))
            {
                Debug.LogError($"[KTexturePacker] atlas 文件为空: {atlasPath}");
                return;
            }

            AtlasData data;
            try
            {
                data = AtlasParser.Parse(json);
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[KTexturePacker] 解析 atlas 失败: {atlasPath}\n{ex.Message}");
                return;
            }

            if (data.Pages == null || data.Pages.Count == 0)
            {
                Debug.LogWarning($"[KTexturePacker] atlas 没有 pages: {atlasPath}");
                return;
            }

            int totalSprites = 0;
            bool hasError = false;

            foreach (AtlasPage page in data.Pages)
            {
                if (page == null || string.IsNullOrEmpty(page.Image)) continue;

                string pngPath = Path.Combine(atlasDir, page.Image);
                pngPath = pngPath.Replace('\\', '/');

                if (!File.Exists(pngPath))
                {
                    Debug.LogError($"[KTexturePacker] 找不到图集页纹理: {pngPath}（atlas 中声明 image=\"{page.Image}\"）");
                    hasError = true;
                    continue;
                }

                TextureImporter importer = AssetImporter.GetAtPath(pngPath) as TextureImporter;
                if (importer == null)
                {
                    Debug.LogError($"[KTexturePacker] 无法获取 TextureImporter: {pngPath}");
                    hasError = true;
                    continue;
                }

                // 设置为 Sprite(2D and UI) 的 Multiple 模式。
                importer.textureType = TextureImporterType.Sprite;
                importer.spriteImportMode = SpriteImportMode.Multiple;
                importer.mipmapEnabled = false;
                importer.filterMode = FilterMode.Point; // 像素艺术默认最近邻，可按需改
                importer.alphaIsTransparency = true;

                // 先应用基础导入设置（textureType / spriteImportMode 等），
                // 这样 importer 才能提供 ISpriteEditorDataProvider。
                AssetDatabase.ImportAsset(pngPath, ImportAssetOptions.ForceUpdate);

                // 重新取回 importer（应用后实例可能已刷新）。
                importer = (TextureImporter)AssetImporter.GetAtPath(pngPath);
                if (importer == null)
                {
                    Debug.LogError($"[KTexturePacker] 应用后无法重新获取 TextureImporter: {pngPath}");
                    hasError = true;
                    continue;
                }

                // Unity 官方写法（2021.2+）：用 SpriteDataProviderFactories 获取 ISpriteEditorDataProvider。
                var factory = new SpriteDataProviderFactories();
                factory.Init();
                ISpriteEditorDataProvider provider = factory.GetSpriteEditorDataProviderFromObject(importer);
                if (provider == null)
                {
                    Debug.LogError($"[KTexturePacker] 无法获取 ISpriteEditorDataProvider: {pngPath}");
                    hasError = true;
                    continue;
                }
                provider.InitSpriteEditorDataProvider();

                int count = (page.Regions != null) ? page.Regions.Count : 0;
                SpriteRect[] spriteRects = new SpriteRect[count];

                for (int i = 0; i < count; i++)
                {
                    AtlasRegion r = page.Regions[i];
                    // 坐标系转换：KTexturePacker 的像素坐标系原点在纹理左上、Y 轴向下（图像坐标）；
                    // 而 Unity 的 Sprite 切片以纹理左下为原点、Y 轴向上。因此 Y 必须翻转：
                    //   unityY = page.Height - ktpY - ktpH
                    // （与 codeandweb 官方导入器把 rect 翻转后再赋给 SpriteRect 的做法一致。）
                    float rectY = page.Height - r.Y - r.H;
                    SpriteRect sr = new SpriteRect
                    {
                        name = r.Name,
                        rect = new Rect(r.X, rectY, r.W, r.H),
                        pivot = new Vector2(0.5f, 0.5f),
                        alignment = (int)SpriteAlignment.Center,
                        border = Vector4.zero,
                        spriteID = GUID.Generate(),
                    };
                    spriteRects[i] = sr;
                }

                // Unity 2021.2+ 需同步维护 Sprite 名称 -> FileID 映射，避免引用丢失。
                // 参考 codeandweb.com 官方 TexturePacker Importer：复用同名旧 GUID，保证引用稳定。
                var nameFileId = provider.GetDataProvider<ISpriteNameFileIdDataProvider>();
                SpriteNameFileIdPair[] ids = new SpriteNameFileIdPair[count];
                for (int i = 0; i < count; i++)
                {
                    GUID guid = GUID.Generate();
                    if (nameFileId != null)
                    {
                        // 若已有同名 Sprite，则复用其旧 GUID，避免引用失效。
                        foreach (var old in nameFileId.GetNameFileIdPairs())
                        {
                            if (old.name == spriteRects[i].name)
                            {
                                guid = old.GetFileGUID();
                                break;
                            }
                        }
                    }
                    spriteRects[i].spriteID = guid;
                    ids[i] = new SpriteNameFileIdPair(spriteRects[i].name, guid);
                }

                provider.SetSpriteRects(spriteRects);
                if (nameFileId != null)
                {
                    nameFileId.SetNameFileIdPairs(ids);
                }

                provider.Apply();

                // 重新导入，使 Sprite 资源真正生成。
                AssetDatabase.ImportAsset(pngPath, ImportAssetOptions.ForceUpdate);
                totalSprites += count;

                Debug.Log($"[KTexturePacker] 已生成 {count} 个 Sprite -> {pngPath}");
            }

            // 仅当所有页都成功解析并生成 Sprite（无任何 error 级错误）时，
            // 才删除图集 JSON（其使命已完成，Sprite 数据已写入各 PNG 的导入设置 .meta）。
            // 一旦解析/生成过程中出现错误，保留该文件以便排查。
            if (hasError)
            {
                Debug.LogWarning($"[KTexturePacker] 解析或生成过程中存在错误，保留图集文件不删除: {atlasPath}");
            }
            else
            {
                if (AssetDatabase.DeleteAsset(atlasPath))
                {
                    Debug.Log($"[KTexturePacker] 解析成功，已删除图集文件: {atlasPath}");
                }
                else
                {
                    Debug.LogWarning($"[KTexturePacker] 解析成功但无法删除图集文件: {atlasPath}");
                }
            }

            AssetDatabase.Refresh();
            Debug.Log($"[KTexturePacker] 完成，共生成 {totalSprites} 个 Sprite。");
        }

        private static TextAsset GetSelectedAtlasTextAsset()
        {
            Object obj = Selection.activeObject;
            if (obj == null || !(obj is TextAsset)) return null;

            string path = AssetDatabase.GetAssetPath(obj);
            if (string.IsNullOrEmpty(path)) return null;

            // 只处理 KTexturePacker 导出的图集文件（约定扩展名 .atlas.txt）。
            if (!path.EndsWith(".atlas.txt", System.StringComparison.OrdinalIgnoreCase))
                return null;

            return (TextAsset)obj;
        }
    }
}
