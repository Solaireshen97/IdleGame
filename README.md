# IdleGame V0.9

基于 .NET 8、ASP.NET Core Web API、Blazor WebAssembly、EF Core 和 SQLite 的网页放置类战斗游戏骨架。

## 项目结构

- `Game.Shared`：共享模型、枚举和 DTO
- `Game.Server`：Web API、SQLite 持久化及领域服务
- `Game.Client`：Blazor WebAssembly 客户端
- `Game.Server.Tests`：服务层测试

## V0.9：多人准备、超时与 Auto

每个房间固定包含 5 个战斗槽位。创建者可在非战斗阶段编入多个自有角色；其他玩家可将自己的当前激活角色加入一个空槽位，每名外来玩家最多占用一个槽位。

- 槽位 1～5 决定行动顺序；角色按升序依次攻击。
- 怪物攻击存活角色中槽位编号最小者。
- 每个角色只能占用一个房间的一个槽位。
- `Preparing` 与 `Cooldown` 期间锁定队伍组成，禁止加入、离开、上阵、替换、移除和切换主控。
- 每名玩家独立点击“准备”，全体存活角色确认后立即结算回合。
- 准备开始 30 秒后，未确认角色只在本回合临时 Auto 并自动结算；该事实通过战斗日志显示，回合结束即清理临时状态。
- 可为自己的存活角色开启永久 Auto。混合队伍使用 10 秒 CD；全员永久 Auto 时每 30 秒自动推进一回合。
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
- `POST /api/rooms/{roomId}/join`：使用当前激活角色加入指定空槽位。请求体为 `slotIndex`。
- `DELETE /api/rooms/{roomId}/leave`：外来玩家离开自己的槽位。
- `POST /api/rooms/{roomId}/slots/{slotIndex}/auto`：设置自己的永久 Auto。请求体为 `isAutoEnabled`。
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

暂不支持职业和技能、Buff/Debuff、仇恨、多怪物、副本奖励、离线结算、快照恢复或 SignalR。副本系统和“副本首通后解锁 Auto”限制尚未实现，当前 Auto 不设解锁条件。
