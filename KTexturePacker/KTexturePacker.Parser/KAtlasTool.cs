using System.Numerics;

namespace KTexturePacker.Parser
{
    //这是已经定义好了的结构
    public struct UVRegion
    {
        public Vector2 bottomLeftUV;
        public Vector2 bottomRightUV;
        public Vector2 topLeftUV;
        public Vector2 topRightUV;
    }

    //这里是导出 可以被游戏引擎利用的数据
    public static class KAtlasTool
    {
        //钟表指针是顺时针旋转的。
        /// <summary>
        /// 计算某个子图在图集中的四角 UV（像素空间，原点在图集左上角）。
        /// 消费方需用其所属图集页的宽高 (page.Width, page.Height) 归一化：
        ///   u = corner.x / pageWidth
        ///   v = 1 - corner.y / pageHeight   // 若需翻转到纹理左下原点（OpenGL / Unity / MonoGame 约定）
        ///
        /// 旋转约定：KTexturePacker（AtlasPacker.RenderAtlas 调用 Skia 的 canvas.RotateDegrees(90, ...)）
        /// 在图集中以 <b>顺时针（Clockwise）90°</b> 存放旋转子图（即原图顶边落在占位矩形右侧）。
        /// 本方法已把这一旋转反向烘焙进四角 UV 中，消费方直接把四角贴到“正向（未旋转）”四边形上即可得到正确朝向。
        /// </summary>
        public static UVRegion GetUVRegion(AtlasRegion mAtlasRegion)
        {
            UVRegion uv = new UVRegion();

            float x0 = mAtlasRegion.X;
            float y0 = mAtlasRegion.Y;
            float x1 = mAtlasRegion.X + mAtlasRegion.W; // 占位矩形右边缘
            float y1 = mAtlasRegion.Y + mAtlasRegion.H; // 占位矩形下边缘

            if (!mAtlasRegion.Rotated)
            {
                // 非旋转：四角即占位矩形（左上、右上、左下、右下）。
                uv.topLeftUV     = new Vector2(x0, y0);
                uv.topRightUV    = new Vector2(x1, y0);
                uv.bottomLeftUV  = new Vector2(x0, y1);
                uv.bottomRightUV = new Vector2(x1, y1);
            }
            else
            {
                // 顺时针 90°：原图像素 (i, j) -> 图集像素 (x0 + W - j, y0 + i)。
                // （W = region.W = 原图高，H = region.H = 原图宽；整边 x1/x0、y1/y0 等价 ±1 像素边界）
                // 正向四边形四角对应的原图像素：
                //   topLeft  (原图顶左 0,0)      -> 图集 (x1, y0)
                //   topRight (原图顶右 w0-1, 0)  -> 图集 (x1, y1)
                //   bottomLeft(原图底左 0, h0-1) -> 图集 (x0, y0)
                //   bottomRight(原图底右 ..)     -> 图集 (x0, y1)
                uv.topLeftUV     = new Vector2(x1, y0);
                uv.topRightUV    = new Vector2(x1, y1);
                uv.bottomLeftUV  = new Vector2(x0, y0);
                uv.bottomRightUV = new Vector2(x0, y1);
            }

            return uv;
        }

        /// <summary>
        /// 计算某个子图已归一化（[0,1]）的四角 UV，需传入其所属图集页以做归一化。
        /// flipY=true（默认）：按 OpenGL / Unity / MonoGame 纹理约定（原点在左下）翻转 V 轴。
        /// 旋转子图同样已按“顺时针 90°”反向烘焙到四角 UV 中。
        /// </summary>
        public static UVRegion GetUV01Region(AtlasRegion region, AtlasPage page, bool flipY = true)
        {
            UVRegion uv = GetUVRegion(region); // 先拿到像素空间四角

            if (page == null) throw new System.ArgumentNullException(nameof(page));
            if (page.Width <= 0 || page.Height <= 0)
                throw new System.ArgumentException("Atlas page width/height must be positive.", nameof(page));

            float invW = 1f / page.Width;
            float invH = 1f / page.Height;

            System.Func<Vector2, Vector2> norm = c =>
            {
                float u = c.X * invW;
                float v = flipY ? (1f - c.Y * invH) : (c.Y * invH);
                return new Vector2(u, v);
            };

            uv.topLeftUV     = norm(uv.topLeftUV);
            uv.topRightUV    = norm(uv.topRightUV);
            uv.bottomLeftUV  = norm(uv.bottomLeftUV);
            uv.bottomRightUV = norm(uv.bottomRightUV);

            return uv;
        }
    }
}
