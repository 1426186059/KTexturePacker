# KTexturePacker

一个免费的图集（Texture Atlas）打包工具，基于 **ASP.NET Core**（原生 AOT 发布）+ **SkiaSharp**。

把一堆散图（png/jpg/gif/bmp/webp/tga）用 MaxRects 算法拼成一张大图，并导出坐标数据，支持通用 JSON 与 libGDX `.atlas` 两种格式。

## 功能

- **MaxRects 装箱**：4 种启发式（`best` / `shortside` / `longside` / `bottomleft`）。
- **旋转支持**：可选允许精灵旋转以更紧凑。
- **边距（padding）**：精灵间留白，避免采样溢出。
- **两种导出格式**：
  - `json`：通用 JSON（帧名 + 矩形 + 源尺寸 + 偏移 + 旋转）。
  - `atlas`：libGDX `.atlas` 格式（配合 `atlas.png`）。
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
  - 预览（返回 PNG）。
  - 打包（返回 `atlas.zip`，内含 `atlas.png` + `atlas.json` 或 `atlas.atlas`）。

## API

| 方法 | 路径 | 说明 |
|------|------|------|
| GET  | `/api/dirs?root=<路径>` | 列出磁盘目录（本地模式用）。 |
| GET  | `/api/preview-local?inputFolder=&maxSize=&padding=&algorithm=&allowRotation=` | 本地路径预览，返回 PNG。 |
| GET  | `/api/pack-local?inputFolder=&outputFolder=&maxSize=&padding=&algorithm=&allowRotation=&format=` | 本地路径打包，写入磁盘。 |
| POST | `/api/preview` (multipart `files`) | 上传预览，返回 PNG。 |
| POST | `/api/pack` (multipart `files`) | 上传打包，返回 zip。 |

**公共参数**：`maxSize`（大图边长上限，默认 2048）、`padding`（默认 1）、`algorithm`（`best`/`shortside`/`longside`/`bottomleft`，默认 `best`）、`allowRotation`（bool，默认 false）、`format`（`json`/`atlas`，默认 `json`）。

## 支持的图片格式

输入：`png`、`jpg/jpeg`、`gif`、`bmp`、`webp`、`tga`。
输出大图：`png`。

## 已知限制

- 本地模式依赖服务本机运行；远程部署请使用上传模式。
- 仅支持单页图集（一张大图）。
