# 战斗背景

六个属性的战斗背景已全部完成：**赤烬荒原**（火）、**雾凇雪境**（水）、**岩芽林地**（土）、**长风原野**（风）、**琉辉之森**（光）与 **墨雾幽林**（暗）各三张，共十八张。均由内置 ImageGen 分别生成，保存在 `Game.Client/wwwroot/art/battle-backgrounds/`。完整提示词见 `prompts.json`。

| 场景 | 资源 | 设计 |
| --- | --- | --- |
| 赤烬荒原区域讨伐 | `red-ember-wastes.png` | 红岩峡谷与赤土地面 |
| 燧渊裂隙 | `suiyuan-rift.png` | 幽暗熔岩洞窟，远处岩壁透出局部火光 |
| 燧渊裂隙·深层LV1 | `suiyuan-ember-core.png` | 深层熔心大厅，远处熔火映照黑曜岩层 |
| 雾凇雪境区域讨伐 | `frostmist-snowlands.png` | 雪山松林、冰湖与暮色山岭 |
| 镜泉冰窟 | `mirror-spring-cavern.png` | 蓝冰洞壁、融冰暗河与幽暗岩冰地面 |
| 镜泉冰窟·深层LV1 | `frost-crown-seat.png` | 深层冰晶大厅，远处冠形冰晶与靛蓝岩层 |
| 岩芽林地区域讨伐 | `stonebud-woodland.png` | 农场边缘的森林、苔岩与开阔土路 |
| 烛井矿窟 | `candlewell-mine.png` | 烛光矿道、远景木支架与赭色矿脉 |
| 烛井矿窟·深层LV1 | `candlewell-depths.png` | 厚重岩层构成的深层采掘大厅与远景矿脉 |
| 长风原野区域讨伐 | `longwind-prairie.png` | 风吹草原、层叠丘陵与横向云带 |
| 裂翎高巢 | `riftfeather-high-nest.png` | 峭壁巢地、远景枝巢与风蚀岩台 |
| 裂翎高巢·深层LV1 | `riftfeather-storm-spire.png` | 云海之上的风暴高台、尖峰与冷色风云 |
| 琉辉之森区域讨伐 | `glassglow-forest.png` | 金叶森林、青绿远景与古老林道 |
| 琉辉旧庭 | `glassglow-old-court.png` | 金叶林中的旧庭遗迹、残拱与褪色金饰 |
| 琉辉旧庭·深层LV1 | `glassglow-prism-hall.png` | 深层棱晶大厅、远景嵌壁晶核与古老石柱 |
| 墨雾幽林区域讨伐 | `inkmist-woodland.png` | 薄雾墓园、枯枝林影与远景断碑 |
| 缄丝峡谷 | `silentsilk-canyon.png` | 幽暗石峡、远景蛛网与枯根 |
| 缄丝峡谷·深层LV1 | `silentsilk-nest-depths.png` | 地下墓穴巢厅、嵌壁残网与旧石拱 |

前景均为完整、平坦的战斗地面，熔岩、暗河和高耸冰晶位于远处，保留角色和首领的站立空间。水属性场景使用较暗的冰蓝地面与有限的雪白高光，保证白色、淡蓝色怪物的辨识度。土属性场景使用苔绿、灰褐和赭金，矿道支架、轨道与矿脉位于后景和边角，避免遮挡战斗位置。

风属性场景通过边缘倾斜的草叶、远处横向云层和风蚀岩柱表现风感。巢地与高台的断崖、云海和枝巢均在战场后方，角色所在的岩台保持完整，中央没有风暴特效。

光属性场景通过金叶树冠、古老石庭与远景棱晶表现辉光氛围。金色、象牙色与晶体高光集中在远景和边缘；前景使用灰褐、灰青与深灰蓝，保持平坦完整，便于辨认金色野兽、浅色构装体和首领。

暗属性场景通过远处薄雾、枯枝林影、墓园断碑与灰白残网表现幽暗氛围。墓碑、蛛网、枯根与旧石拱集中在远景和边缘；角色所在的地面保持完整平坦，以中等明度的灰青色区分黑紫蜘蛛与深灰野兽，前景不覆盖浓雾或蛛网。

背景仅显示在角色与怪物所在的 `.battle-field` 中，使用独立装饰层；角色、血条和战斗特效位于它上方。背景没有指针交互，并有轻量的暗色遮罩，保证状态文字可读。四周通过渐隐遮罩自然融入面板底色；中央保持清晰像素细节，边缘过渡宽度随战场尺寸调整。

`subjects.json` 按当前世界配置的区域或副本代码指定资源。`tools/build_battle_background_manifest.py` 从 `Game.Server/world.json` 获取当前名称，生成 `manifest.json` 与 `Game.Client/Services/BattleBackgroundArt.cs`。六个区域各覆盖 9 个普通讨伐和精英讨伐；十二个副本各使用一个独立的 `dungeon` 条目，覆盖其全部遭遇。共映射 66 个战斗入口。

`preview.html` 使用项目战斗 CSS 与原有角色、怪物 PNG，提供十八种场景、手机/宽屏和单人/五人队伍切换，用于检查裁切与人物辨识度。副本预览使用对应首领与当前生命值、波次数。

更新映射：

```powershell
python tools/build_battle_background_manifest.py
```
