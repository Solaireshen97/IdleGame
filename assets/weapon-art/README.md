# 武器像素图标

当前武器目录的 99 件武器各有一张 192×192 透明 PNG。图标根据武器外形分为 12 种母版，按火、水、土、风、光、暗属性着色，并对每件武器加入稳定的小型光点变化。界面仍用原有品质边框区分普通、精良、稀有和史诗。

- `masters/`：使用内置 ImageGen 生成的 12 张透明原图。
- `manifest.json`：武器编号、名称、属性与所用外形的对应表。
- `preview.png`：12 种外形的总览。
- `../../Game.Client/wwwroot/art/weapons/`：游戏实际加载的 99 张 PNG。

重新导出：

```powershell
python tools/build_weapon_art.py
```

脚本从 `Game.Server/appsettings.json` 读取武器目录，并同时生成 `Game.Client/Services/WeaponArt.cs`。新增武器名称如使用全新的外形用词，需在 `weapon_shape` 中补充规则或母版。

## 生成图像时使用的提示词

内置 ImageGen 模式，对每种外形单独生成一张。共同提示词：

> Use case: stylized-concept. Asset type: production-ready fantasy RPG inventory icon. Exactly one isolated weapon, centered, full object visible, diagonal tilt if natural. Original HD-2D inspired pixel art: deliberate blocky square-pixel clusters, strong readable silhouette, jewel-toned highlights, rich but restrained lighting. Designed as a 64x64 pixel sprite scaled up with hard nearest-neighbor edges. Genuine transparent alpha background and generous padding. No scene, card, text, border, people, hand, blur, antialias smoothing, watermark.

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
