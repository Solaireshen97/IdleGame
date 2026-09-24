# 药剂与草药像素图

当前目录包含 10 种药剂或药水、19 种草药，共 29 张独立 PNG。游戏资源放在 `Game.Client/wwwroot/art/items/potions/` 和 `Game.Client/wwwroot/art/items/herbs/`，通过 `Game.Client/Services/ItemArt.cs` 按物品代码引用。

图像使用内置 ImageGen 为每件物品单独生成。统一方向为经典 RPG 背包图标：大块清晰像素、深蓝轮廓、暖金高光、冷青阴影、透明背景；药剂保持容器与液体的区别，草药保留花、叶、根或孢子的形态差异。通用提示词如下，其中 `{name}`、`{subject}` 和 `{type}` 由 `manifest.json` 填入：

> Use case: stylized-concept. Asset type: a production-ready inventory icon for a cozy classic RPG. Subject: {name}, specifically {subject}. Exactly one isolated {type} centered, fully visible, at an isometric three-quarter angle. Match the approved class and weapon pixel art: handcrafted 16-bit RPG pixels, large deliberate square clusters, dark navy outline, warm amber highlights, cool teal shadows, restrained 20-color palette and a clear silhouette readable at 40px. Draw as a true 48x48 game icon enlarged with crisp nearest-neighbor edges. Transparent alpha background. No hand, person, landscape, ground, card frame, label, text, watermark, glossy smooth shading, gradients, blur, or antialiasing.

`subjects.json` 是物品造型描述；`manifest.json` 是生成后的物品清单。目录更新后运行 `python tools/build_item_manifest.py` 校验并更新客户端映射。运行 `python tools/build_item_preview.py` 可重建检查图集。
