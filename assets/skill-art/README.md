# 角色技能像素图

当前职业目录包含 25 个主动技能，图标存放于 `Game.Client/wwwroot/art/skills/`。目录同时保留历史技能素材，不应通过 PNG 总数推断当前技能数量。技能编成、战斗技能卡、技能详情，以及天赋树里负责解锁技能的节点，都通过 `Game.Client/Services/SkillArt.cs` 按技能代码取图。

图标使用内置 ImageGen 逐个生成，延续当前项目的经典 RPG 像素风：大块清晰像素、深蓝轮廓、暖金高光、冷色阴影和透明背景。设计重点是缩小后的动作辨识度，因此斩击、连斩、招架、截击和各类圣光技能使用不同的主体轮廓，而不是只更换颜色。

通用提示词结构如下，`{name}`、`{subject}` 和 `{palette}` 取自 `manifest.json`：

> Use case: stylized-concept. Asset type: one production-ready character ability icon for a cozy classic fantasy RPG. Primary request: design the unique icon for {name}. Subject: {subject}. Exactly one compact isolated magical or combat emblem, centered and fully visible, filling most of a square canvas. Style/medium: handcrafted 16-bit RPG pixel art matching the approved character, weapon, potion and herb assets; large deliberate square pixel clusters, dark navy outer outline, strong internal contrast, restrained 16-24 color palette, readable at 40px. Color palette: {palette}. Composition: dynamic diagonal energy where appropriate, clean silhouette with generous transparent edge padding. Genuine transparent alpha background. No character portrait, full person, hand holding the icon, landscape, floor, card frame, circular badge, text, letters, numbers, watermark, smooth vector curves, gradients, blur or antialiasing.

2026-10-02 为振奋精神、神圣光环、寒冰箭、奥术领域、破绽射击、协猎信号、鹰眼时刻、肾上腺素新增独立图标，文件使用当前技能代码命名。此次提示词保存在 `mobile-refresh-20261002.json`。这些图标分别突出双目标治疗、持续光环、冰晶弹道、奥术法阵、护甲弱点、协作信号、鹰眼印记和连击加速，避免沿用群疗、冰盾、毒箭或闪避图造成误读。

`subjects.json` 保存每个技能的图形概念及 `file` 映射；省略 `file` 时使用 `{code}.png`。历史条目允许保留。`manifest.json` 和 `Game.Client/Services/SkillArt.cs` 都由生成器整体重写，请不要只手动修改生成文件。

技能目录或素材变化后，先运行 `python tools/build_skill_manifest.py --check`，只校验而不写文件。检查通过后运行 `python tools/build_skill_manifest.py` 更新清单和客户端映射。生成器校验当前技能代码/素材条目无重复、引用文件位于技能素材目录且存在，以及 PNG 签名、头信息、数据块完整性、校验和与压缩数据；不会删除历史素材。运行 `python -m unittest discover -s tools -p test_build_skill_manifest.py` 可执行校验回归测试。

运行 `python tools/build_skill_preview.py` 可重新生成检查图集；旧图集不会随映射自动更新。缩小后的辨识度仍需在实际界面中以 32px 和 40px 检查，文件有效不等于视觉效果合格。
