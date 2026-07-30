# KTexturePacker

一个免费的图集（Texture Atlas）打包工具，基于 **ASP.NET Core**（原生 AOT 发布）+ **SkiaSharp**。

把一堆散图（png/jpg/gif/bmp/webp/tga）用 MaxRects 算法拼成一张或多张大图（自动分页），并导出坐标数据，使用 **libGDX `.atlas` 单文件格式**（一个 `.atlas.json` 含所有 page）。

## 功能

- **MaxRects 装箱**：4 种启发式（`best` / `shortside` / `longside` / `bottomleft`）。
- **旋转支持**：可选允许精灵旋转以更紧凑。
- **边距（padding）**：精灵间留白，避免采样溢出。
- **多页图集（自动分页）**：当图片在单张 `maxSize` 大图里放不下时，自动开新的一页继续塞，直到全部放下。
- **单一导出格式**：libGDX `.atlas` 文本——**一个 `.atlas.json` 文件包含全部 page**，配合多张 `atlas_0.png`、`atlas_1.png`… 使用。
- **两种工作模式**：本地模式（默认，按磁盘路径读图）、远程模式（上传图片）。

## 架构

| 项目 | 说明 |
|------|------|
| `KTexturePacker.Core` | 类库：MaxRectsPacker、AtlasPacker（Skia 合成）、AtlasExporter、PackerSettings。引用 SkiaSharp 4.150.1。 |
| `KTexturePacker.Web` | ASP.NET Core Minimal API（`PublishAot=true`），提供 Web UI 与打包接口。 |

## 运行

```bash
dotnet run --project KTexturePacker.Web/KTexturePacker.Web.csproj
# 或发布为原生单文件：
dotnet publish -c Release -r win-x64 KTexturePacker.Web/KTexturePacker.Web.csproj
```

打开 `http://localhost:5000` 即可使用。

> **AOT 发布注意**：原生 exe 的 ContentRoot 取"启动工作目录"，须从 publish 目录运行才能找到 `wwwroot`（UI）。部署时 `cd` 到发布目录再启动 exe。

## 两种模式

UI 顶部可切换：

### 本地模式（默认）

- **适用前提**：Web 服务运行在**你本机**（`localhost`）。
- **原理**：直接填磁盘**文件夹全路径**，由后端用 `System.IO` 读盘，无需上传。
- **能力**：
  - 浏览磁盘目录（`/api/dirs`）。
  - 预览打包结果（返回 PNG + `X-Atlas-Width/Height` 头）。
  - 打包并**直接写入服务器磁盘**指定输出文件夹。
- **限制**：浏览器本身无法用磁盘路径读文件（沙箱安全限制）。因此本地模式只有在服务本机时才有意义；部署到远程机器时该模式无效，请改远程模式。

### 远程模式

- **适用场景**：服务部署在远端，处理本机图片。
- **原理**：在浏览器中选/拖图片（或整个文件夹，递归收集），上传到服务器打包后回传 zip / 预览 PNG。
- **能力**：
  - 拖拽或选择文件 / 文件夹上传。
  - 预览（返回拼合 PNG，多页上下拼成一张，头含 `X-Page-Count`）。
  - 打包（返回 `atlas.zip`，内含 `atlas_0.png`…`atlas_N-1.png` + 单个 `atlas.atlas.json`）。

## 输出结构（多页）

- 多张大图：`atlas_0.png`、`atlas_1.png`、…（每页一张）。
- 描述文件：**单个 `atlas.atlas.json`**，按 page 分块，每块以图片名（`atlas_0.png`…）开头，多页之间空行分隔。可直接被 libGDX 运行时加载。
- 预览：所有页上下拼成一张 PNG（页间 8px 透明缝），便于单次请求查看全部。

## 旋转方向与坐标系

- **像素坐标系**：图集大图采用标准图像像素坐标——原点在**左上角**，X 轴向**右**递增，Y 轴向**下**递增。所有导出的子图坐标（`x`、`y`、`w`、`h`，以及 `sourceW`、`sourceH`）均以此为基准。
- **旋转方向**：开启「允许 90° 旋转」时，MaxRects 可能把精灵旋转后放入图集。绘制使用 Skia 的 `canvas.RotateDegrees(90, ...)`。由于 Skia 坐标系 **Y 轴向下**，**正角度即屏幕上的顺时针方向**，因此旋转子图在图集中按 **顺时针（Clockwise）90°** 存放——即原图的顶边（第一行像素）落在占位矩形的右侧。
- **对解析库的影响**：`KTexturePackerParser` 的 `KAtlasTool.GetUVRegion` 已把这一顺时针 90° **反向烘焙进四角 UV** 中。消费方直接把 `UVRegion` 的 `topLeftUV / topRightUV / bottomLeftUV / bottomRightUV` 贴到“正向（未旋转）”的四边形四角上，即可得到正确朝向，无需再手动旋转几何体。
- **UV 的 V 轴约定**：`GetUVRegion(region, page, flipY: true)`（默认）按 OpenGL / Unity / MonoGame 纹理约定翻转 V 轴（纹理原点在左下）。若你的管线纹理原点已是左上（如按像素直接采样），传入 `flipY: false`。
- **单参重载**：`GetUVRegion(AtlasRegion)` 返回**像素空间**四角（原点图集左上），由消费方按各自图集页尺寸（`page.Width / page.Height`）自行归一化。

## API

| 方法 | 路径 | 说明 |
|------|------|------|
| GET  | `/api/dirs?path=<路径>` | 列出磁盘目录（本地模式用）。 |
| GET  | `/api/preview-local?inputFolder=&maxSize=&padding=&algorithm=&allowRotation=` | 本地路径预览，返回拼合 PNG。 |
| GET  | `/api/pack-local?inputFolder=&outputFolder=&maxSize=&padding=&algorithm=&allowRotation=` | 本地路径打包，写入磁盘。 |
| POST | `/api/preview` (multipart `files`) | 上传预览，返回拼合 PNG。 |
| POST | `/api/pack` (multipart `files`) | 上传打包，返回 zip。 |

**公共参数**：`maxSize`（单页大图边长上限，默认 2048，放不下自动分页）、`padding`（默认 1）、`algorithm`（`best`/`longside`/`bottomleft`/`contact`，默认 `best`）、`allowRotation`（bool，默认 false）。

**响应头（预览/打包）**：`X-Atlas-Width` / `X-Atlas-Height`（首页尺寸）、`X-Sprite-Count`（已放置总数）、`X-Page-Count`（页数）、`X-Unplaced-Count`（放不下的图片数，通常为 0；若单张超过 `maxSize` 则 > 0 并提示）。

## 支持的图片格式

输入：`png`、`jpg/jpeg`、`gif`、`bmp`、`webp`、`tga`。
输出大图：`png`。

## 已知限制

- 本地模式依赖服务本机运行；远程部署请使用上传模式。
- 单张图片本身超过 `maxSize` 时无法放入任何一页，会被丢弃并提示「N 张无法放入」。此时需调大 `maxSize` 或拆分该图。
