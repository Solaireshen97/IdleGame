# 武器像素图标

当前武器目录的 132 件武器均有透明 PNG。原有 99 件使用 12 种母版导出的 192×192 图标；新增的 3 把职业初始武器和 30 把精英副本武器由内置 ImageGen 逐件生成独立造型，保留生成的透明通道。全部沿用职业角色图的深蓝轮廓、暖金高光和清晰方形像素。界面用品质边框区分普通、精良、稀有和史诗。

- `masters/`：使用内置 ImageGen 生成的 12 张透明原图。
- `manifest.json`：全部武器编号、名称、属性、文件与图像来源的对应表。
- `subjects.json`：33 张独立图标的造型描述、配色和来源分组。
- `preview.png`：12 种外形的总览。
- `added-weapons-preview.png`：33 张新增独立图标的总览。
- `../../Game.Client/wwwroot/art/weapons/`：游戏实际加载的 132 张 PNG。

校验图像文件并更新清单和名称映射：

```powershell
python tools/build_weapon_manifest.py
python tools/build_weapon_preview.py
```

清单脚本从 `Game.Server/appsettings.json` 读取武器目录，并生成 `Game.Client/Services/WeaponArt.cs`。未规划造型或缺少 PNG 时会报错，避免目录扩充后静默漏图。`build_weapon_art.py` 只用于恢复缺失的旧母版派生图，保留所有已存在的图像和新增独立图；日常更新使用上面的清单脚本。

## 新增独立图标的提示词

使用内置 ImageGen，每张一次请求，设置 `transparent_background=true`。`{subject}` 与 `{palette}` 来自 `subjects.json`：

> Use case: stylized-concept. Asset type: one isolated fantasy RPG weapon inventory icon for an existing pixel-art game. Subject: {subject}. Colors: {palette}. Exactly one complete weapon only, centered, diagonal lower-left grip to upper-right head where appropriate, entire silhouette and bowstring visible. Match the existing simple classic RPG weapon icons: genuinely coarse handcrafted 48x48 pixel-art design enlarged with sharp nearest-neighbor square pixels, dark navy outline, warm amber highlights, cool teal shadows, 16-24 flat palette colors, 2-3 shades per material. Simple practical shape readable at 48px, generous clear padding, compact proportions. Preserve the weapon type. Genuine transparent alpha background. No character, hand, extra item, scenery, floor, pedestal, card, frame, text, watermark, decorative particles, surrounding aura, photorealism, 3D, smooth gradients, blur, tiny engraving or anti-aliased thin strokes.

## 生成图像时使用的提示词

使用内置 ImageGen，为每种外形单独生成一张。长剑以旧图为编辑目标、以剑士职业图为风格参考；其余外形采用共同提示词并分别指定武器：

> Use case: stylized-concept. Asset type: a single isolated fantasy RPG weapon inventory icon. Draw in the SAME pixel-art visual language as a classic medium-chibi swordsman sprite: large clearly visible square pixel clusters, dark navy outlines, aged brass accents, warm amber highlights, limited palette of 20-30 flat colors, 2-3 shades per material, strong readable silhouette at 64px. Imagine a hand-drawn 48x48 pixel canvas enlarged with nearest-neighbor square pixels. Cozy 16-bit RPG style, no realistic textures, no smooth gradients, no detailed engraving, no thin anti-aliased strokes, no dithering noise, no 3D rendering. Genuine transparent alpha background, generous clear padding, exactly one item only, no character, card, text, frame or watermark.

每张图另加对应的 `Subject` 描述：

| 母版 | Subject |
| --- | --- |
| sword | One elegant one-handed longsword, full blade and hilt visible, tempered steel, aged brass, dark leather. |
| dagger | A short curved assassin's dagger with dark iron blade, brass guard and leather grip. |
| staff | A tall wooden mage's staff crowned by a pale crystal, bound in aged bronze rings. |
| scepter | An ornate short ritual scepter with a faceted gemstone and brass filigree. |
| saber | A curved single-edged saber with polished steel and a dark wrapped grip. |
| bow | A graceful recurved hunting bow carved from dark wood, taut string, bronze tips, no arrow. |
| hammer | A heavy two-handed warhammer with broad squared iron head, chiseled stone accents and leather-bound haft. |
| axe | A rugged single-head battle axe with dark forged steel edge, brass fittings and hardwood haft. |
| spear | A long hunting spear with bright leaf-shaped steel tip, leather lashings and wooden shaft. |
| crossbow | A compact medieval hand crossbow with dark wood stock, bronze bow arms, taut string, no bolt. |
| pickaxe | A miner's pickaxe with sharp forged iron double-point head, hardwood handle and brass collar. |
| club | A primitive but crafted wooden war club with iron bands, leather wrapped handle, and a bone inlay. |

导出脚本不调用图像生成服务。
