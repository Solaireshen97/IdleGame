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
- 创建房间时可选择单次或重复战斗。重复模式在胜利 30 秒后由服务端重开副本，恢复怪物及队伍角色的全部 HP；战败后停止。
- 重开后的每个回合仍遵守现有的准备与 Auto 规则。服务端定时推进超时、全员 Auto 和重复重开，房间页关闭后也能继续；需要手动准备的队伍会等待玩家确认。
- 双方仍存活时，进入 10 秒 `Cooldown`。
- 房间版本号是 EF Core 并发令牌，确保并发回合最多一个结算成功。

主控角色当前仅为队伍标识，不影响伤害、行动顺序和权限。

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
- `POST /api/battle/reset`、`POST /api/battle/heal`：保留的测试入口。

## 本地运行

```bash
dotnet restore IdleGame.sln
dotnet build IdleGame.sln
dotnet run --project Game.Server/Game.Server.csproj
dotnet run --project Game.Client/Game.Client.csproj
```

## 后续范围

暂不支持职业和技能、Buff/Debuff、仇恨、多怪物、副本奖励、离线结算、快照恢复或 SignalR。
