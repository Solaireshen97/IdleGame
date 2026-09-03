# IdleGame V0.7

基于 .NET 8、ASP.NET Core Web API、Blazor WebAssembly、EF Core 和 SQLite 的网页放置类战斗游戏骨架。

## 项目结构

- `Game.Shared`：共享模型、枚举和 DTO
- `Game.Server`：Web API、SQLite 持久化及领域服务
- `Game.Client`：Blazor WebAssembly 客户端
- `Game.Server.Tests`：服务层测试

## V0.7：单用户多角色编队

每个房间固定包含 5 个战斗槽位。创建者的当前激活角色会自动进入 1 号位并成为主控角色。房间仅允许房主将自己的多个角色上阵。

- 槽位 1～5 决定行动顺序；角色按升序依次攻击。
- 怪物攻击存活角色中槽位编号最小者。
- 每个角色只能占用一个房间的一个槽位。
- 仅 `NotStarted` 或 `BattleOver` 状态可上阵、替换、移除或切换主控；冷却中禁止调整。
- 怪物死亡或全队死亡后，房间进入 `BattleOver`。
- 双方仍存活时，进入 10 秒 `Cooldown`。
- 房间版本号是 EF Core 并发令牌，确保并发回合最多一个结算成功。

主控角色当前仅为队伍标识，不影响伤害、行动顺序和权限。

## API

- `POST /api/rooms`：创建房间，并初始化 5 个槽位。
- `GET /api/rooms/{roomId}`：获取怪物、战斗状态和完整槽位信息。
- `POST /api/rooms/{roomId}/slots`：上阵或替换自有角色。请求体为 `slotIndex`、`characterId`。
- `DELETE /api/rooms/{roomId}/slots/{slotIndex}`：移除非主控槽位角色。
- `POST /api/rooms/{roomId}/main-control`：设置已上阵的自有角色为主控。请求体为 `characterId`。
- `POST /api/battle/round`：执行整队回合。
- `POST /api/battle/reset`、`POST /api/battle/heal`：保留的测试入口。
- `POST /api/rooms/{roomId}/join`：已弃用，不再写入成员数据。

## 本地运行

```bash
dotnet restore IdleGame.sln
dotnet build IdleGame.sln
dotnet run --project Game.Server/Game.Server.csproj
dotnet run --project Game.Client/Game.Client.csproj
```

## 后续范围

暂不支持其他用户加入、准备确认、Auto、30 秒超时、技能、Buff/Debuff、仇恨、多怪物、副本奖励、离线结算、快照恢复或 SignalR。这些能力保留给后续版本。
