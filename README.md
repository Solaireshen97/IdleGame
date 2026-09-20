# IdleGame V1.0

基于 .NET 8、ASP.NET Core Web API、Blazor WebAssembly、EF Core 和 SQLite 的网页放置类战斗游戏骨架。

## 项目结构

- `Game.Shared`：共享模型、枚举和 DTO
- `Game.Server`：Web API、SQLite 持久化及领域服务
- `Game.Client`：Blazor WebAssembly 客户端
- `Game.Server.Tests`：服务层测试

## V1.0：副本、首通、准备超时与 Auto

每个房间固定包含 5 个战斗槽位。创建者可在非战斗阶段编入多个自有角色；其他玩家可将自己的当前激活角色加入一个空槽位，每名外来玩家最多占用一个槽位。

- 槽位 1～5 决定行动顺序；角色按升序依次攻击。
- 怪物攻击存活角色中槽位编号最小者。
- 每个角色只能占用一个房间的一个槽位。
- `Preparing` 与 `Cooldown` 期间锁定队伍组成，禁止加入、离开、上阵、替换、移除和切换主控。
- 每名玩家独立点击“准备”，全体存活角色确认后立即结算回合。
- 房间基于副本创建，默认提供史莱姆平原、哥布林营地和狼群森林；每个房间按副本模板生成独立怪物实例。
- 击败怪物时，所有参与用户获得该副本的一条首通记录，重复通关不会重复记录。
- 永久 Auto 仅限已通关当前副本的用户开启。准备超时的临时自动确认不代表解锁永久 Auto。
- 混合队伍强制 30 秒准备超时；纯自建队由房主决定是否启用该超时。全员已解锁并开启永久 Auto 时每 30 秒自动推进一回合。
- 怪物死亡或全队死亡后，房间进入 `BattleOver`。
- 战斗结束后，仅房主可手动重置房间；重置会恢复怪物及全队角色的 HP。重复战斗胜利后的等待阶段由服务端自动重开，不能手动跳过。
- 创建房间时可选择单次或重复战斗。重复模式在胜利 30 秒后由服务端重开副本，恢复怪物及队伍角色的全部 HP；战败后停止。
- 重开后的每个回合仍遵守现有的准备与 Auto 规则。服务端定时推进超时、全员 Auto 和重复重开，房间页关闭后也能继续；需要手动准备的队伍会等待玩家确认。
- 双方仍存活时，进入 10 秒 `Cooldown`。
- 房间版本号是 EF Core 并发令牌，确保并发回合最多一个结算成功。

主控角色当前仅为队伍标识，不影响伤害、行动顺序和权限。

## 战斗消耗品

- 背包和两个战斗补给槽均属于角色。不同角色的库存、装备及自动使用设置相互独立；只有角色本人能读取或修改。
- `Consumables` 配置段定义道具效果、冷却组、冷却回合数和各副本胜利掉落。当前示例为小型治疗药水：回复 20 HP、治疗组冷却 3 回合，每名参战角色每次胜利获得 1 瓶。奖励与经验、战斗结果在同一次数据库提交中保存。
- 手动选择补给槽会把指令排入下一次战斗结算，不会立即扣库存或回复 HP；传入空槽号可取消。每名角色每回合最多使用一个道具，使用道具不取代攻击。角色攻击后若怪物仍存活，先使用道具，再承受怪物攻击；战斗已胜利时不消耗道具。
- 自动使用与角色的 Auto 准备独立。装备时可选择是否自动使用及 HP 百分比阈值；手动使用可越过自动阈值，但仍需角色存活、HP 未满、库存充足且冷却结束。
- 同一冷却组的道具共享冷却。第 0 回合使用冷却 3 回合的道具后，第 1～3 回合不可使用，第 4 回合恢复可用。手动重置或重复战斗重开会清除该房间的冷却，不会补回已消耗的库存。
- 房间详情只向角色所属玩家返回该角色的补给槽、库存和剩余冷却；所有使用条件和库存扣减由服务端验证并执行。
- 左侧导航的“战斗补给”页面可查看当前操作角色的库存、装备道具和设置自动使用阈值。战斗房间的“行动指令”区可为自己的上阵角色手动排队或取消使用，并显示库存及剩余冷却。

## 职业技能与天赋树

- 现有角色迁移为骑士；新角色可选择骑士或牧师。每个职业有两项免费起始技能，技能效果和冷却在 `Skills` 配置段中调整。
- 每名角色有独立的五栏技能配置，可在“职业天赋”页装备、调整优先级和设置自动使用。治疗与守护技能可设置目标 HP 阈值。战斗进行期间锁定技能栏。
- 手动可为下一回合排队多个技能，与自动准备无关；手动指令优先，其余自动技能按 1～5 号位检查。一次结算中满足条件的多个不同技能均可触发，不占普通攻击或药水使用。
- 结算顺序为普通攻击、技能、药水、怪物攻击。每次实际施放才进入自身冷却；手动重置和重复战斗重开会清除冷却。所有资格、目标、阈值、冷却和排队归属均由服务端检查。
- 骑士与牧师各有一棵可配置的三层职业天赋树：先点亮根节点，再选择左右分支；顶层节点需要两个分支均已点亮。节点消耗升级获得的天赋点，与攻击、防御、生命加点共用同一余额。
- 点亮节点后学会对应技能，仍需装备到五栏技能栏中。重置职业树会按实际花费返还点数，卸下失去解锁资格的技能；基础属性加点由其原有重置按钮单独处理。战斗进行期间不能修改职业树或技能栏。
- 职业、技能数值、天赋节点成本、层级和前置关系在 `Skills` 配置段维护。后端校验职业归属、前置、点数余额、装备资格与战斗使用资格，客户端只负责展示和操作。
- 技能栏与待执行指令只向角色所属玩家返回。

## API

- `GET /api/dungeons`、`GET /api/dungeons/{dungeonId}`：获取副本配置及当前用户首通和 Auto 解锁状态。
- `POST /api/rooms`：使用 `dungeonId` 和可选的 `isRepeatBattle` 创建房间，并按副本容量初始化槽位；旧 `monsterType` 请求仍兼容，默认单次战斗。
- `GET /api/rooms/{roomId}`：获取怪物、战斗状态和完整槽位信息。
- `POST /api/rooms/{roomId}/slots`：上阵或替换自有角色。请求体为 `slotIndex`、`characterId`。
- `DELETE /api/rooms/{roomId}/slots/{slotIndex}`：移除非主控槽位角色。
- `POST /api/rooms/{roomId}/main-control`：设置已上阵的自有角色为主控。请求体为 `characterId`。
- `POST /api/rooms/{roomId}/join`：使用当前激活角色加入指定空槽位。请求体为 `slotIndex`。
- `DELETE /api/rooms/{roomId}/leave`：外来玩家离开自己的槽位。
- `POST /api/rooms/{roomId}/slots/{slotIndex}/auto`：设置自己的永久 Auto。请求体为 `isAutoEnabled`。
- `POST /api/rooms/{roomId}/preparation-timeout`：房主设置纯自建队的准备超时偏好。请求体为 `isEnabled`。
- `POST /api/battle/prepare`：确认当前用户的存活角色；全员确认后自动结算。
- `POST /api/battle/sync`：推进准备超时及全员 Auto 回合。
- `POST /api/battle/reset`：房主在战斗结束后手动重置；战斗进行中或重复战斗胜利等待期间会拒绝请求。
- `GET /api/user/characters/{characterId}/consumables`：读取自有角色的背包数量、道具定义和两个补给槽。
- `PUT /api/user/characters/{characterId}/consumables/{slotIndex}`：设置补给槽，`slotIndex` 为 1 或 2；请求体为 `itemCode`（可为 `null`）、`autoUseEnabled`、`autoHpThresholdPercent`（1～100）。战斗进行时不能调整装备。
- `POST /api/battle/consumable`：排队或取消下一回合的手动道具使用；请求体为 `roomId`、`characterId`、`consumableSlotIndex`（1、2 或 `null`）。成功后返回房间详情。
- `GET /api/skills/professions`：获取可创建的职业。
- `GET /api/user/characters/{characterId}/skills`、`PUT /api/user/characters/{characterId}/skills/{slotIndex}`：读取和设置自有角色的技能栏、自动使用条件。
- `POST /api/user/characters/{characterId}/skills/swap`：交换两个技能栏位及其自动设置。
- `POST /api/user/characters/{characterId}/skills/talents/{nodeCode}/unlock`：消耗天赋点，按前置关系点亮职业技能节点。
- `POST /api/user/characters/{characterId}/skills/talents/reset`：重置职业树并返还点数。
- `POST /api/battle/skill`：手动排队或取消一个技能栏位；请求体为 `roomId`、`characterId`、`skillSlotIndex`、`isQueued`。

## 本地运行

```bash
dotnet restore IdleGame.sln
dotnet build IdleGame.sln
dotnet run --project Game.Server/Game.Server.csproj
dotnet run --project Game.Client/Game.Client.csproj
```

## 后续范围

后续计划包括职业转职、持续性 Buff/Debuff、仇恨、多怪物、离线结算、快照恢复和 SignalR。
