using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace KTexturePacker.MonoGame
{
    public class SpriteRender
    {
        private SpriteBatch spriteBatch;

        public SpriteRender(SpriteBatch spriteBatch)
        {
            this.spriteBatch = spriteBatch;
        }
        
        public void Draw(SpriteFrame sprite, Vector2 position, Color? color = null, float rotation = 0, float scale = 1, SpriteEffects spriteEffects = SpriteEffects.None)
        {
            spriteBatch.Draw(sprite, position, color, rotation, scale, spriteEffects);
        }

    }
}