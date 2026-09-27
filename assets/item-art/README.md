# 物品与魂印像素图

当前目录包含 10 种药剂或药水、19 种草药、1 种武器碎片、6 种 Boss 魂印，共 36 张独立透明 PNG。游戏资源放在 `Game.Client/wwwroot/art/items/` 下的 `potions/`、`herbs/`、`fragments/` 与 `soul-imprints/`，通过 `Game.Client/Services/ItemArt.cs` 按物品代码引用。兑换商店、魂印装备页、武器资源栏和战斗魂印按钮共用这套映射。

图像使用内置 ImageGen 为每件物品单独生成。统一方向为经典 RPG 背包图标：大块清晰像素、深蓝轮廓、暖金高光、冷青阴影、透明背景；药剂保持容器与液体的区别，草药保留花、叶、根或孢子的形态差异。通用提示词如下，其中 `{name}`、`{subject}` 和 `{type}` 由 `manifest.json` 填入：

> Use case: stylized-concept. Asset type: a production-ready inventory icon for a cozy classic RPG. Subject: {name}, specifically {subject}. Exactly one isolated {type} centered, fully visible, at an isometric three-quarter angle. Match the approved class and weapon pixel art: handcrafted 16-bit RPG pixels, large deliberate square clusters, dark navy outline, warm amber highlights, cool teal shadows, restrained 20-color palette and a clear silhouette readable at 40px. Draw as a true 48x48 game icon enlarged with crisp nearest-neighbor edges. Transparent alpha background. No hand, person, landscape, ground, card frame, label, text, watermark, glossy smooth shading, gradients, blur, or antialiasing.

`subjects.json` 是物品造型描述；`manifest.json` 是生成后的物品清单。目录更新后运行 `python tools/build_item_manifest.py` 校验目录覆盖和文件存在性，并更新客户端映射。运行 `python tools/build_item_preview.py` 可重建全部图标预览，以及碎片和魂印专用的 `enhancement-preview.png`。

## 武器碎片与 Boss 魂印

使用内置 ImageGen，每张独立生成，设置 `transparent_background=true`。六枚魂印分别以矿核、毒核、熔火战印、冰心、雷羽、黎明棱晶为主体，保留能力与首领主题的区别。下列模板的 `{name}`、`{subject}` 来自 `manifest.json`，`{type}` 为 `weapon-fragment cluster` 或 `boss soul relic`：

> Use case: stylized-concept. Asset type: one production-ready fantasy RPG inventory icon. Subject: {name}, specifically {subject}. Exactly one isolated compact {type}, centered, entirely visible, filling most of a square with clear transparent edge padding. Match the approved weapon, potion and skill pixel art: classic cozy 16-bit RPG style, genuinely coarse handcrafted 48x48 game sprite enlarged with sharp nearest-neighbor square pixels, large deliberate pixel clusters, dark navy outer outline, warm amber highlights, cool teal shadows, restrained 16-24 flat palette colors, 2-3 shades per material. Strong distinctive silhouette readable at 40px. Genuine transparent alpha background. No boss portrait, face, character, hands, landscape, floor, pedestal, card, frame, text, numbers, watermark, surrounding aura, realistic texture, 3D rendering, smooth gradient, blur, tiny engraving or antialiasing.
