# KTexturePacker

一个免费的图集（Texture Atlas）打包工具，基于 **ASP.NET Core**（原生 AOT 发布）+ **SkiaSharp**。

把一堆散图（png/jpg/gif/bmp/webp/tga）用 MaxRects 算法拼成一张或多张大图（自动分页），导出 PNG 大图 + 坐标描述文件，并直接写入服务器磁盘。

核心特色是两大处理能力：**单文件夹处理** 与 **多文件夹批量处理**——尤其是**多文件夹批量处理**：一次操作即可把根目录下所有子图集目录全部打包输出，这是同类工具（如 TexturePacker）所不具备的一键批量能力。

> **只支持本地模式（LOCAL-ONLY）**：服务与浏览器必须运行在同一台机器（`localhost`）。前端通过填写/选择服务器本机磁盘文件夹路径来打包，**不支持任何远程文件上传**。

## 核心能力

### 一、单文件夹处理

针对一个图集文件夹（内含散图），打包成**一个图集**（可自动分页为多张）。

- **MaxRects 装箱**：4 种启发式算法可切换（`best` / `long` / `bottomleft` / `contact`）。
- **可选 90° 旋转**：允许精灵顺时针旋转 90° 以提升填充率。
- **留白（padding）**：精灵间留白，避免采样溢出。
- **多页自动分页**：超过单页 `maxSize` 时自动开新页，直到全部放下。
- **两种导出格式**：
  - 通用格式（`pages`/`regions` 自描述结构），后缀 `.atlas.txt` 或 `.atlas.json`；
  - PixiJS v8 `Spritesheet` 格式（`.atlas.json`），多页时每页各写一个独立 Spritesheet JSON。
- **先预览后打包**：预览只生成分页缩略图、不写盘，确认效果后再落盘。
- **便捷输入**：输入框支持拖拽文件夹自动识别路径，也可用内置「浏览…」目录选择器逐级选择。
- **命名灵活**：图集名字可手动指定，留空则默认取文件夹名。

### 二、多文件夹处理（TexturePacker 没有的能力）

实际项目中，素材常按「角色 / 场景 / UI / 特效…」拆到多个子目录，每个子目录将来要打成**一个独立图集**。同类工具（如 TexturePacker）需要**逐个目录手动打包**；本工具把这一过程变成**一次点击**：

> 选一个**根目录** → 其下**每个子文件夹**各自是一个图集源 → 一次性把**全部子图集**打包输出。

- **一键批量打包**：无需逐个文件夹重复操作，一次请求处理根目录下所有子图集。
- **统一参数，独立命名**：打包参数（最大边长 / 留白 / 算法 / 旋转 / 导出格式）对所有子图集统一生效；每个图集**自动用子文件夹名作为前缀**，无需逐个命名。
- **批量预览**：一次预览所有子图集，每个以折叠卡片展示分页效果与状态（✓ 成功 / ✗ 失败及原因）。
- **逐项结果报告**：打包完成逐个子图集报告结果与输出路径。
- **互不影响**：某个子文件夹无图片或图片放不下时单独标记失败，**不影响其他子图集的打包**。
- **输出干净**：所有子图集平铺输出到同一个输出文件夹（`hero_0.png` / `hero.atlas.txt`、`npc_0.png` / `npc.atlas.txt`…），目录层级一目了然。

## 页面结构（3 个页面）

| 页面 | 文件 | 说明 |
|------|------|------|
| 主页面 | `wwwroot/index.html` | 两个菜单入口：单文件夹处理 / 多文件夹处理 |
| 单文件夹处理 | `wwwroot/single.html` | 上述「核心能力 · 一」的完整操作界面 |
| 多文件夹处理 | `wwwroot/multi.html` | 上述「核心能力 · 二」的完整操作界面 |

三个页面共用同一套顶部导航，可随时切换。

## 架构

| 项目 | 说明 |
|------|------|
| `KTexturePacker.Core` | 类库：MaxRectsPacker、AtlasPacker（Skia 合成）、AtlasExporter、PackerSettings。引用 SkiaSharp 4.150.1。 |
| `KTexturePacker.Web` | ASP.NET Core Minimal API（`PublishAot=true`），提供 Web UI 与打包接口。 |

## 运行

```bash
dotnet run --project KTexturePacker.Web/KTexturePacker.Web.csproj
# 或发布为原生单文件 exe：
dotnet publish -c Release -r win-x64 KTexturePacker.Web/KTexturePacker.Web.csproj
```

启动后按控制台输出的 URL（默认 `http://localhost:5000`）打开浏览器，填入/选择服务器本机文件夹路径即可使用。

> **AOT 发布注意**：原生 exe 的 ContentRoot 取「启动工作目录」，须从 publish 目录运行才能找到 `wwwroot`（UI）。部署时 `cd` 到发布目录再启动 exe。

## 输出结构（多页）

以图集名字 `atlas` 为例：

- 大图：`atlas_0.png`、`atlas_1.png`、…（每页一张）。
- 描述文件（通用格式）：单个 `atlas.atlas.txt`（或 `atlas.atlas.json`），含全部 page 的 regions 坐标。
- 描述文件（PixiJS v8）：主文件 `atlas.atlas.json`（含第 0 页），多页时每页另写 `atlas_1.atlas.json`、`atlas_2.atlas.json`…（与 `related_multi_packs` 约定一致）。

多文件夹模式下，每个子文件夹各自生成一组上述文件，前缀 = 子文件夹名，全部平铺在输出文件夹中。

> 打包时会自动跳过本工具自己导出的 `atlas.png` / `atlas_0.png`… 产物，避免「输出目录 = 输入目录」时上一轮产物被再次喂入导致图集越滚越大。

## 旋转方向与坐标系

- **像素坐标系**：图集大图采用标准图像像素坐标——原点在**左上角**，X 轴向**右**递增，Y 轴向**下**递增。所有导出的子图坐标（`x`、`y`、`w`、`h`，以及 `sourceW`、`sourceH`）均以此为基准。
- **旋转方向**：开启「允许 90° 旋转」时，MaxRects 可能把精灵旋转后放入图集。绘制使用 Skia 的 `canvas.RotateDegrees(90, ...)`。由于 Skia 坐标系 **Y 轴向下**，**正角度即屏幕上的顺时针方向**，因此旋转子图在图集中按 **顺时针（Clockwise）90°** 存放——即原图的顶边（第一行像素）落在占位矩形的右侧。
- **对解析库的影响**：`KTexturePackerParser` 的 `KAtlasTool.GetUVRegion` 已把这一顺时针 90° **反向烘焙进四角 UV** 中。消费方直接把 `UVRegion` 的 `topLeftUV / topRightUV / bottomLeftUV / bottomRightUV` 贴到"正向（未旋转）"的四边形四角上，即可得到正确朝向，无需再手动旋转几何体。
- **UV 的 V 轴约定**：`GetUVRegion(region, page, flipY: true)`（默认）按 OpenGL / Unity / MonoGame 纹理约定翻转 V 轴（纹理原点在左下）。若你的管线纹理原点已是左上（如按像素直接采样），传入 `flipY: false`。
- **单参重载**：`GetUVRegion(AtlasRegion)` 返回**像素空间**四角（原点图集左上），由消费方按各自图集页尺寸（`page.Width / page.Height`）自行归一化。

## API

| 方法 | 路径 | 说明 |
|------|------|------|
| GET | `/api/dirs?path=<路径>` | 列出磁盘目录（前端「浏览…」选择器用）。 |
| GET | `/api/preview?inputFolder=&outputFolder=&maxSize=&padding=&algorithm=&allowRotation=&atlasName=` | 单文件夹预览，返回分页缩略图 JSON（不写盘）。 |
| GET | `/api/pack?inputFolder=&outputFolder=&maxSize=&padding=&algorithm=&allowRotation=&atlasName=&format=&suffix=` | 单文件夹打包，写入磁盘。 |
| GET | `/api/multi-preview?rootFolder=&maxSize=&padding=&algorithm=&allowRotation=` | 多文件夹预览，返回每个子图集的缩略图与状态 JSON。 |
| GET | `/api/multi-pack?rootFolder=&outputFolder=&maxSize=&padding=&algorithm=&allowRotation=&format=&suffix=` | 多文件夹打包，逐个子图集写入磁盘。 |

**公共参数**：

- `maxSize`：单页大图边长上限（默认 2048，放不下自动分页）。
- `padding`：留白 px（默认 1）。
- `algorithm`：`best`（Best Short Side Fit，默认）/ `long`（Best Long Side Fit）/ `bottomleft`（Bottom-Left）/ `contact`（Contact Point）。
- `allowRotation`：bool，默认 `false`。
- `format`：`generic`（默认，通用 pages/regions）/ `pixijs`（PixiJS v8 Spritesheet）。
- `suffix`：描述文件后缀 `.atlas.txt` / `.atlas.json`（留空按格式取默认：PixiJS = `.atlas.json`，其余 = `.atlas.txt`）。

**响应（预览，单文件夹）**：JSON `{ pages:[{page,w,h,realW,realH,png}], count, realPages:[{w,h}] }`，其中 `png` 为 base64 缩略图（最长边 ≤ 512px，仅用于浏览器渲染，不影响导出），`realW/realH` 为实际图集页尺寸。

**响应（预览，多文件夹）**：JSON `{ items:[{name,error} | {name,pages,count,realPages}], okCount, failCount, totalPages, totalSprites, totalUnplaced }`。

**响应头（预览）**：`X-Atlas-Width` / `X-Atlas-Height`（首页尺寸）、`X-Sprite-Count`（已放置总数）、`X-Page-Count`（页数）、`X-Unplaced-Count`（放不下的图片数）、`X-Atlas-Name`（单文件夹模式导出的前缀名）。

## 支持的图片格式

输入：`png`、`jpg/jpeg`、`gif`、`bmp`、`webp`、`tga`。
输出大图：`png`。

## 已知限制

- **只支持本地模式**：服务须运行在你本机，浏览器通过磁盘路径访问（浏览器自身无法用磁盘路径读文件，因此远程部署时该模式不适用）。
- 单张图片本身超过 `maxSize` 时无法放入任何一页，会被丢弃并提示「N 张无法放入」。此时需调大 `maxSize` 或拆分该图。
- 多文件夹模式下，某个子图集失败（无图片 / 图片放不下）不会中断其他子图集的打包，结果会逐项列出。
