# KTexturePacker.Unity

把 KTexturePacker 导出的图集导入 Unity 并自动切成 Multiple Sprite。

## 目录约定

- `Assets/NeedDll/KTexturePacker.Parser.dll`：零依赖图集解析库（netstandard2.1，Unity IL2CPP/AOT 安全，无外部引用）。
- `Assets/Editor/KTexturePackerAtlasImporter.cs`：编辑器工具。
- `Assets/Atlas/<name>/<name>.atlas.txt`：图集 JSON（`pages[].{image,width,height,regions[]}`）。
- `Assets/Atlas/<name>/<name>_0.png`：对应的纹理 PNG（文件名 = 图集 JSON 中 `page.Image`）。

> 注意：Unity 把 `<name>.atlas.txt` 当作 `TextAsset`（文本导入），文件名必须保留 `.atlas.txt` 后缀，编辑器才会识别。

## 用法

1. 在 Project 窗口选中 `*.atlas.txt` 文件。
2. 顶部菜单 `Assets / KTexturePacker / Generate Atlas Sprite`。
3. 工具会找到同目录下 `page.Image` 指向的 PNG，将其导入设置改为：
   - `Texture Type = Sprite (2D and UI)`
   - `Sprite Mode = Multiple`
   - 按图集 JSON 的 `regions[]` 写入每个子图的 `SpriteMetaData`（name / rect / pivot）。
4. 在纹理的 Sprite Editor 里即可看到所有切片。

工具**只修改 PNG 的导入设置（.meta），不读取也不重写磁盘上的 PNG 像素**。

## 坐标与旋转约定

- **坐标系**：KTexturePacker 的像素坐标系原点在纹理左上、Y 轴向下，与 Unity `TextureImporter` 的 `spriteSheet.rect` 坐标系一致，故 `rect` 直接填入 `x/y/w/h`。
- **旋转**：若打包时开启了「允许 90° 旋转」，子图在纹理中是**顺时针（Clockwise）90°** 存放（Skia `RotateDegrees(90)`）。Unity 的 `SpriteMetaData.rect` 不支持旋转表达，本工具按纹理中的实际占位矩形（`x/y/w/h`）直接切片，**不处理旋转朝向**——请在打包时按需关闭旋转，或自行处理纹理。
