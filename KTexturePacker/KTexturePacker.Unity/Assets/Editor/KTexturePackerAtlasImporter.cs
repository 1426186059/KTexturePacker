using System.IO;
using UnityEditor;
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

            foreach (AtlasPage page in data.Pages)
            {
                if (page == null || string.IsNullOrEmpty(page.Image)) continue;

                string pngPath = Path.Combine(atlasDir, page.Image);
                pngPath = pngPath.Replace('\\', '/');

                if (!File.Exists(pngPath))
                {
                    Debug.LogError($"[KTexturePacker] 找不到图集页纹理: {pngPath}（atlas 中声明 image=\"{page.Image}\"）");
                    continue;
                }

                TextureImporter importer = AssetImporter.GetAtPath(pngPath) as TextureImporter;
                if (importer == null)
                {
                    Debug.LogError($"[KTexturePacker] 无法获取 TextureImporter: {pngPath}");
                    continue;
                }

                // 设置为 Sprite(2D and UI) 的 Multiple 模式。
                importer.textureType = TextureImporterType.Sprite;
                importer.spriteImportMode = SpriteImportMode.Multiple;
                importer.mipmapEnabled = false;
                importer.filterMode = FilterMode.Point; // 像素艺术默认最近邻，可按需改
                importer.alphaIsTransparency = true;

                int count = (page.Regions != null) ? page.Regions.Count : 0;
                SpriteMetaData[] metas = new SpriteMetaData[count];

                for (int i = 0; i < count; i++)
                {
                    AtlasRegion r = page.Regions[i];
                    SpriteMetaData meta = new SpriteMetaData
                    {
                        // 纹理坐标系原点在左上、Y 轴向下，与 KTexturePacker 一致，直接填。
                        rect = new Rect(r.X, r.Y, r.W, r.H),
                        name = r.Name,
                        alignment = (int)SpriteAlignment.Center,
                        pivot = new Vector2(0.5f, 0.5f),
                        border = Vector4.zero,
                    };
                    metas[i] = meta;
                }

                // 设置 Sprite 切片。优先用 spritesheet（从早期 Unity 一直稳定的 API），
                // 若当前程序集解析不到该属性，再用 SerializedObject 兜底，避免 NullReferenceException。
                bool applied = false;
                try
                {
                    importer.spritesheet = metas;
                    applied = true;
                }
                catch (System.Exception ex)
                {
                    Debug.LogWarning($"[KTexturePacker] importer.spritesheet 不可用，回退 SerializedObject: {ex.Message}");
                }

                if (!applied)
                {
                    SerializedObject so = new SerializedObject(importer);
                    SerializedProperty spritesProp = so.FindProperty("m_SpriteSheet.m_Sprites");
                    if (spritesProp != null)
                    {
                        spritesProp.ClearArray();
                        for (int i = 0; i < metas.Length; i++)
                        {
                            spritesProp.InsertArrayElementAtIndex(i);
                            SerializedProperty elem = spritesProp.GetArrayElementAtIndex(i);
                            SerializedProperty p;
                            p = elem.FindPropertyRelative("name"); if (p != null) p.stringValue = metas[i].name;
                            p = elem.FindPropertyRelative("rect"); if (p != null) p.rectValue = metas[i].rect;
                            p = elem.FindPropertyRelative("alignment"); if (p != null) p.intValue = metas[i].alignment;
                            p = elem.FindPropertyRelative("pivot"); if (p != null) p.vector2Value = metas[i].pivot;
                            p = elem.FindPropertyRelative("border"); if (p != null) p.vector4Value = metas[i].border;
                        }
                        so.ApplyModifiedProperties();
                        applied = true;
                    }
                }

                if (!applied)
                {
                    Debug.LogError($"[KTexturePacker] 无法写入 Sprite 切片到: {pngPath}（两路 API 均不可用）");
                    continue;
                }

                AssetDatabase.ImportAsset(pngPath, ImportAssetOptions.ForceUpdate);
                totalSprites += count;

                Debug.Log($"[KTexturePacker] 已生成 {count} 个 Sprite -> {pngPath}");
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
