# 角色技能像素图

当前职业目录中的 34 个主动技能各有一张独立 PNG，存放于 `Game.Client/wwwroot/art/skills/`。技能编成、战斗技能卡、技能详情，以及天赋树里负责解锁技能的节点，都通过 `Game.Client/Services/SkillArt.cs` 按技能代码取图。

图标使用内置 ImageGen 逐个生成，延续当前项目的经典 RPG 像素风：大块清晰像素、深蓝轮廓、暖金高光、冷色阴影和透明背景。设计重点是缩小后的动作辨识度，因此斩击、连斩、招架、截击和各类圣光技能使用不同的主体轮廓，而不是只更换颜色。

通用提示词结构如下，`{name}`、`{subject}` 和 `{palette}` 取自 `manifest.json`：

> Use case: stylized-concept. Asset type: one production-ready character ability icon for a cozy classic fantasy RPG. Primary request: design the unique icon for {name}. Subject: {subject}. Exactly one compact isolated magical or combat emblem, centered and fully visible, filling most of a square canvas. Style/medium: handcrafted 16-bit RPG pixel art matching the approved character, weapon, potion and herb assets; large deliberate square pixel clusters, dark navy outer outline, strong internal contrast, restrained 16-24 color palette, readable at 40px. Color palette: {palette}. Composition: dynamic diagonal energy where appropriate, clean silhouette with generous transparent edge padding. Genuine transparent alpha background. No character portrait, full person, hand holding the icon, landscape, floor, card frame, circular badge, text, letters, numbers, watermark, smooth vector curves, gradients, blur or antialiasing.

`subjects.json` 保存每个技能的图形概念，`manifest.json` 是最终清单。技能目录变化后运行 `python tools/build_skill_manifest.py` 校验并更新客户端映射；运行 `python tools/build_skill_preview.py` 可重新生成检查图集。
