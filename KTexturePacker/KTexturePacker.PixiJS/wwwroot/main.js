// KTexturePacker · PixiJS 图集示例
// 演示：加载 KTexturePacker 导出的 PixiJS Spritesheet（MyRes/Atlas），
//       Spritesheet 解析 -> 动画播放（animations）+ 全部帧静态预览。
import { Application, Assets, AnimatedSprite, Sprite, Spritesheet } from './vendor/pixi.min.mjs';

const ATLAS_PNG = 'MyRes/Atlas/Bonus_0.png';      // KTexturePacker 导出的大图
const ATLAS_JSON = 'MyRes/Atlas/Bonus.atlas.txt'; // KTexturePacker 导出的描述文件（PixiJS 格式）

const info = document.getElementById('info');
const setInfo = (t) => info.innerHTML = t;

async function main() {
  try {
    // 1) 创建 Pixi 应用
    const app = new Application();
    await app.init({ width: 640, height: 480, background: '#1a1c22', antialias: false });
    document.getElementById('stage').appendChild(app.canvas);

    // 2) 加载图集 PNG（整张纹理）
    const sheetTexture = await Assets.load(ATLAS_PNG);

    // 3) 加载图集描述 JSON 并交给 PixiJS Spritesheet 解析
    //    （自动处理 frame/rotated/trimmed/sourceSize/animations）
    const res = await fetch(ATLAS_JSON);
    if (!res.ok) throw new Error(`加载 ${ATLAS_JSON} 失败: HTTP ${res.status}`);
    const atlasData = await res.json();
    const sheet = new Spritesheet(sheetTexture, atlasData);
    await sheet.parse();

    const frameKeys = Object.keys(sheet.textures);
    const animKeys = Object.keys(sheet.animations || {});
    if (!animKeys.length) throw new Error('图集 JSON 里没有 animations 元数据');

    // 4) 播放第一个动画（KTexturePacker 写入的 animations 数组）
    const anim = new AnimatedSprite(sheet.animations[animKeys[0]]);
    anim.animationSpeed = 0.12;      // 帧速率（12 FPS）
    anim.loop = true;
    anim.anchor.set(0.5);
    anim.scale.set(4);               // 30x28 -> 放大 4 倍便于观察
    anim.position.set(140, 240);
    app.stage.addChild(anim);
    anim.play();

    // 5) 全部帧静态预览（排成 3 列 2 行，放大 3 倍）
    const COLS = 3, SPACING_X = 100, SPACING_Y = 120;
    frameKeys.forEach((k, i) => {
      const s = new Sprite(sheet.textures[k]);
      s.scale.set(3);
      s.position.set(280 + (i % COLS) * SPACING_X, 100 + Math.floor(i / COLS) * SPACING_Y);
      app.stage.addChild(s);
    });

    // 6) 信息展示
    const meta = atlasData.meta || {};
    const size = meta.size || {};
    setInfo(
      `图集 <b>${ATLAS_JSON.split('/').pop()}</b>：大图 <b>${size.w}×${size.h}</b>，帧 <b>${frameKeys.length}</b>，` +
      `动画 <b>${animKeys.join(', ')}</b><br>` +
      `左侧为动画 ${animKeys[0]}（${sheet.animations[animKeys[0]].length} 帧，循环播放），右侧为全部帧静态预览`
    );
  } catch (e) {
    console.error(e);
    setInfo('<span class="err">加载失败：' + (e && e.message || e) + '</span>');
  }
}

main();
