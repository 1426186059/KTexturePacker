using KTexturePacker.Parser;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Content;
using Microsoft.Xna.Framework.Graphics;
using System.IO;

namespace KTexturePacker.MonoGame
{
    public class SpriteSheetLoader
    {
        private readonly ContentManager contentManager;

        public SpriteSheetLoader(ContentManager contentManager)
        {
            this.contentManager = contentManager;
        }
        
        public SpriteSheet Load(string imageResource)
        {
            string dataFile = Path.Combine(contentManager.RootDirectory, $"{imageResource}.atlas");
            if (!File.Exists(dataFile))
            {
                dataFile = Path.Combine(contentManager.RootDirectory, $"{imageResource}.atlas.txt");
            }

            string source = File.ReadAllText(dataFile);
            AtlasData mData = AtlasParser.Parse(source);

            SpriteSheet spriteSheet = new SpriteSheet();
            foreach (var v in mData.Pages)
            {
                Texture2D texture = contentManager.Load<Texture2D>($"{Path.GetFileNameWithoutExtension(v.Image)}");
                foreach (var v2 in v.Regions)
                {
                    bool isRotated = v2.Rotated;
                    string name = v2.Name;
                    Rectangle sourceRect = new Rectangle(v2.X, v2.Y, v2.W, v2.H);
                    Vector2 size = new Vector2(v2.SourceW, v2.SourceH);
                    Vector2 pivotPoint = new Vector2(0, 0);
                    SpriteFrame sprite = new SpriteFrame(texture, sourceRect, size, pivotPoint, isRotated);
                    spriteSheet.Add(name, sprite);
                }
            }

            return spriteSheet;
        }
    }
}