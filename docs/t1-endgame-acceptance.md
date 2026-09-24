# T1 终局与职业补全验收

本页记录五个基础职业、十条转职路线与六个终局副本的可复跑验收。模拟器使用正式战斗、怪物行为、掉落与奖励服务，在内存数据库中按固定种子推进时间，不连接玩家数据库。

## 职业完整性

| 基础职业 | 转职一 | 转职二 | 精英队伍职责 |
| --- | --- | --- | --- |
| 剑士 | 骑士 | 战士 | 承伤、短冷却打断与近战爆发 |
| 祭司 | 牧师 | 审判官 | 治疗、净化、沉默与团队易伤 |
| 法师 | 元素使 | 奥术师 | 多段爆发、破甲、驱散与伤害压制 |
| 猎人 | 神射手 | 兽王 | 标记、持续伤害、备用打断与攻防协同 |
| 盗贼 | 刺客 | 诡术师 | 单体爆发、持续伤害、短冷却打断与控场 |

每个基础职业都有两个起始技能、三列基础天赋与两个 10 级免费转职。每条转职都会立即授予一个进阶技能，技能仍受五栏配置、冷却和服务端资格校验约束。

## 终局难度规则

终局目标是三波、五怪的 Lv.10 精英副本，不是地图上的单个 Lv.10 精英怪。六个副本都不检查队伍人数，而是通过 54,000 生命的首领、职责机制和时间压力形成软门槛：

- 第 22 回合开始，首领每隔 3 回合发动一次不可打断的全体软狂暴。
- 第 42 回合开始，首领每隔 2 回合发动一次 900% 倍率的不可打断硬狂暴，不能靠治疗无限拖延。
- 狗头人深层检验破甲、持续输出和前排承伤；瘟疫地穴检验中毒、持续伤害和净化；怒焰熔心检验预告爆发、群体减伤和治疗。
- 霜王座检验打断、减速和技能节奏；风暴巢穴检验多目标压力、追击和快速处理；晨曦核心检验首领护盾、增益驱散和爆发窗口。

“稳定 Auto”不是“固定三个种子里勉强通关”的同义词。本轮回归同时要求 18/18，并以六区平均回合均低于软狂暴触发线作为稳定收尾基线。

## 战斗样本

| 验收集 | 配装、控制方式与规模 | 样本 | 结果 | 六区平均回合范围 |
| --- | --- | ---: | ---: | ---: |
| 普通讨伐 | 首周参考；10 路线 × 6 属性 × 3 种子 | 180 | 180/180 | — |
| 区域正式副本 | 首周参考；10 路线 × 6 属性 × 3 种子 | 180 | 180/180 | — |
| Lv.10 单个区域精英 | 首周参考；10 路线 × 6 属性 × 3 种子 | 180 | 180/180 | — |
| 终局单人边界 | 普通 T1 Auto；10 路线 × 6 属性 | 60 | 0/60 | 34.2～41.0 |
| 终局双人边界 | 骑士＋牧师，普通 T1 Auto | 18 | 0/18 | 37.0～41.7 |
| 终局三人挑战 | 骑士＋牧师＋元素使，普通 T1 手动策略 | 18 | 1/18 | 30.0～39.0 |
| 终局四人手动 | 加入神射手，普通 T1 手动策略 | 18 | 15/18 | 26.3～32.0 |
| 终局五人手动 | 再加入诡术师，普通 T1 手动策略 | 18 | 18/18 | 21.0～25.3 |
| 普通装备 Auto 压力样本 | 同一五人队，普通 T1 Auto | 18 | 18/18 | 21.0～25.0 |
| 稳定 Auto | 每人两件区域精英武器、对应魂印，五人 Auto | 18 | 18/18 | 16.7～19.0 |

这组结果对应讨论中的分层：1～2 人无法通过；三人只有极少数手动挑战样本成功；四人普通成型队在手动操作下有合理通关率；五人普通成型队可以稳定手动通关。普通装备五人 Auto 能在这组固定样本中清关，但暗区平均仍到第 25 回合、整体平均消耗 4.9 瓶药水，因此不把它登记成稳定无人值守基线。精英准备队的六区平均全部在第 22 回合前，平均消耗降至 2.2 瓶，才登记为稳定 Auto。

手动模式会读取当前首领预告，提前排队防御、治疗、净化、驱散、打断和魂印；房间推进仍由模拟器自动完成，以保证固定种子可复现。它验证的是回合决策收益，不测量玩家实际点击速度。

原始数据：

- `t1-profession-balance-final.json`：普通讨伐、正式副本和单个区域精英，共 540 场。
- `t1-endgame-solo-boundary.json`、`t1-endgame-party2-final.json`：单人和双人失败边界。
- `t1-endgame-party3-final.json`、`t1-endgame-party4-final.json`、`t1-endgame-party-final.json`：三、四、五人手动策略。
- `t1-endgame-auto-entry.json`、`t1-endgame-auto-stable.json`：普通装备与精英准备 Auto 对照。

## 复跑命令

```powershell
dotnet build tools/Game.BalanceSimulator/Game.BalanceSimulator.csproj --no-restore
dotnet run --project tools/Game.BalanceSimulator/Game.BalanceSimulator.csproj --no-build -- --runs 3 --elements Fire,Water,Earth,Wind,Light,Dark --roles knight,warrior,priest,inquisitor,elementalist,arcanist,marksman,beastmaster,assassin,trickster --stages week --targets normal,dungeon,elite --output docs/t1-profession-balance-final.json
dotnet run --project tools/Game.BalanceSimulator/Game.BalanceSimulator.csproj --no-build -- --runs 1 --elements Fire,Water,Earth,Wind,Light,Dark --roles knight,warrior,priest,inquisitor,elementalist,arcanist,marksman,beastmaster,assassin,trickster --stages week --targets endgame --party 1 --mode auto --output docs/t1-endgame-solo-boundary.json
dotnet run --project tools/Game.BalanceSimulator/Game.BalanceSimulator.csproj --no-build -- --runs 3 --elements Fire,Water,Earth,Wind,Light,Dark --roles balanced-team --stages week --targets endgame --party 2 --mode auto --output docs/t1-endgame-party2-final.json
dotnet run --project tools/Game.BalanceSimulator/Game.BalanceSimulator.csproj --no-build -- --runs 3 --elements Fire,Water,Earth,Wind,Light,Dark --roles balanced-team --stages week --targets endgame --party 3 --mode manual --output docs/t1-endgame-party3-final.json
dotnet run --project tools/Game.BalanceSimulator/Game.BalanceSimulator.csproj --no-build -- --runs 3 --elements Fire,Water,Earth,Wind,Light,Dark --roles balanced-team --stages week --targets endgame --party 4 --mode manual --output docs/t1-endgame-party4-final.json
dotnet run --project tools/Game.BalanceSimulator/Game.BalanceSimulator.csproj --no-build -- --runs 3 --elements Fire,Water,Earth,Wind,Light,Dark --roles balanced-team --stages week --targets endgame --party 5 --mode manual --output docs/t1-endgame-party-final.json
dotnet run --project tools/Game.BalanceSimulator/Game.BalanceSimulator.csproj --no-build -- --runs 3 --elements Fire,Water,Earth,Wind,Light,Dark --roles balanced-team --stages week --targets endgame --party 5 --mode auto --output docs/t1-endgame-auto-entry.json
dotnet run --project tools/Game.BalanceSimulator/Game.BalanceSimulator.csproj --no-build -- --runs 3 --elements Fire,Water,Earth,Wind,Light,Dark --roles balanced-team --stages raid --targets endgame --party 5 --mode auto --output docs/t1-endgame-auto-stable.json
```

这些结果是固定配装和固定随机种子的回归基线，不代表真实获取时间或无限期挂机胜率。长期经济仍以获取流程模拟、试玩和线上遥测为准。

`tools/t1-economy-projection.mjs` 另以这十条路线的首周实测循环运行了六属性各 2000 次、共 60 组固定 82 小时刷取投影；所有组在模型内达到资源条件。该结果只说明固定首周配装下长期收益不构成明显断档，不能代替从注册开始的动态成长模拟。
