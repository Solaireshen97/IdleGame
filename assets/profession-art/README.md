# 职业角色图

当前有 5 个基础职业和 10 个转职，共 15 张独立立绘。游戏加载的透明 PNG 位于 `../../Game.Client/wwwroot/art/professions/`，`preview.png` 用于检查它们在相同显示尺寸下的比例与风格。`subjects.json` 记录造型，`manifest.json` 与 `Game.Client/Services/ProfessionArt.cs` 由 `tools/build_profession_manifest.py` 根据职业配置生成。

图像使用内置 ImageGen 制作。剑士采用约 3.5 头身的中等 Q 版比例、清晰像素块、深蓝布料和暖金高光；其他职业以此为风格基准，保持相同视角、站姿和像素密度，再按职业区分装备：

| 职业 | 图像要素 |
| --- | --- |
| 剑士 | 深蓝短袍、红围巾、简易护肩、单手剑 |
| 祭司 | 蓝白行旅袍、木制短杖、朴素圣职者造型 |
| 骑士 | 蓝灰重甲、蓝披风、盾牌、短剑 |
| 战士 | 深铁与皮革护具、红腰带、双手战斧 |
| 牧师 | 蓝白金礼袍、法杖、治疗光点 |

新增基础职业为法师、猎人、盗贼；新增转职为审判官、元素使、奥术师、神射手、兽王、刺客、诡术师。提示词共同约束：`hand-built square pixel clusters; medium chibi proportions; 16-bit RPG pixel-art feel; front three-quarter camera; full-body idle stance; transparent alpha; no card, scene, text, frame, smooth gradients or antialiased edges`。每个职业各生成一张。

重新生成预览：

```powershell
python tools/build_profession_preview.py
```
