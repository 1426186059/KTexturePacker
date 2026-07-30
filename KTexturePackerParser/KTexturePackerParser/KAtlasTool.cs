using System.Numerics;

namespace KTexturePacker.Parser
{
    public sealed class UVRegion
    {
        public Vector2 bottomLeftUV { get; set; }
        public Vector2 bottomRightUV { get; set; }
        public Vector2 topLeftUV { get; set; }
        public Vector2 topRightUV { get; set; }
    }

    //这里是导出 可以被游戏引擎利用的数据
    public static class KAtlasTool
    {
        public static UVRegion GetUVRegion(AtlasRegion mAtlasRegion)
        {
            UVRegion mm = new UVRegion();
            return mm;
        }
    }
}
