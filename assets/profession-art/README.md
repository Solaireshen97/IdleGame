# 职业角色图

当前有 2 个基础职业和 3 个转职：剑士、祭司、骑士、战士、牧师。游戏加载的透明 PNG 位于 `../../Game.Client/wwwroot/art/professions/`，`preview.png` 用于检查五张图在相同显示尺寸下的比例与风格。

图像使用内置 ImageGen 制作。剑士采用约 3.5 头身的中等 Q 版比例、清晰像素块、深蓝布料和暖金高光；其余四张以剑士原图作风格参考，保持相同视角、站姿和像素密度，再按职业区分装备：

| 职业 | 图像要素 |
| --- | --- |
| 剑士 | 深蓝短袍、红围巾、简易护肩、单手剑 |
| 祭司 | 蓝白行旅袍、木制短杖、朴素圣职者造型 |
| 骑士 | 蓝灰重甲、蓝披风、盾牌、短剑 |
| 战士 | 深铁与皮革护具、红腰带、双手战斧 |
| 牧师 | 蓝白金礼袍、法杖、治疗光点 |

提示词共同约束：`hand-built square pixel clusters; medium chibi proportions; 16-bit RPG pixel-art feel; front three-quarter camera; full-body idle stance; transparent alpha; no card, scene, text, frame, smooth gradients or antialiased edges`。每个职业各生成一张，未使用同一图像改色充数。

重新生成预览：

```powershell
python tools/build_profession_preview.py
```
