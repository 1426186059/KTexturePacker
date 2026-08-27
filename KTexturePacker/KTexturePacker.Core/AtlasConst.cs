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

    }
}
