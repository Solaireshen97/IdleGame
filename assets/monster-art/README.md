# 怪物像素图

当前遭遇配置中的 97 种怪物各有一张独立 PNG，存放在 `Game.Client/wwwroot/art/monsters/`。战斗、状态弹窗和副本怪物详情通过 `Game.Client/Services/MonsterArt.cs` 按名称取图。原来的四张通用怪物 SVG 占位图已删除。

风格沿用已定稿的职业与武器图：经典 RPG 的中等 Q 版比例，清晰的大块像素、深蓝轮廓、暖金高光和冷青阴影；怪物分别保留名称所指的物种、装备及属性特征。所有成品都保留透明背景。

图像使用内置 ImageGen 逐种生成，每种怪物单独请求一次。通用提示词结构如下，`{name}`、`{subject}` 和 `{elemental accents}` 取自 `manifest.json`：

> Use case: stylized-concept. Asset type: one production-ready fantasy RPG enemy sprite for battle UI. Subject: {name}, specifically {subject}. One distinct enemy only, full body visible, front three-quarter view facing LEFT toward heroes, ready stance, feet or lowest body edge on a consistent baseline. Match the approved medium-chibi class pixel-art direction: cozy classic 16-bit RPG sprite, hand-placed large square pixel clusters, dark navy outline, warm amber highlights, cool teal shadows, 20-30 deliberate palette colors, strong silhouette readable at 100px. Elemental accents: {elemental accents}. Build like a 64x64 sprite enlarged with crisp nearest-neighbor squares. Genuine transparent alpha background. No ground, scenery, frame, card, text, watermark, smooth gradients, photo texture, blur or antialiasing.

`subjects.json` 记录每种怪物的外形描述，`manifest.json` 是名称到文件的对应表。遭遇配置变动后，运行 `python tools/build_monster_manifest.py` 校验并更新映射；全部图像就位后，运行 `python tools/build_monster_preview.py` 生成便于检查的总图集。
