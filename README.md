# IdleGame V0

最基础可运行版本（V0）的网页放置类自动战斗游戏骨架，基于 .NET 8、ASP.NET Core Web API、Blazor WebAssembly、EF Core + SQLite。

## 项目结构

- `/Game.Shared`：共享模型、枚举、DTO（仅数据结构）
- `/Game.Server`：后端 API、SQLite 持久化、数据初始化、战斗/房间服务
- `/Game.Client`：Blazor WASM 前端页面与 API 调用
- `/IdleGame.sln`：解决方案文件

## 本地运行

1. 还原与编译：
   ```bash
   dotnet restore IdleGame.sln
   dotnet build IdleGame.sln
   ```
2. 启动后端（默认 `http://localhost:5115`）：
   ```bash
   dotnet run --project Game.Server/Game.Server.csproj
   ```
3. 启动前端（默认 `http://localhost:5154`）：
   ```bash
   dotnet run --project Game.Client/Game.Client.csproj
   ```
4. 打开前端页面后会加载 `roomId=1`，点击“开始战斗”可触发单次战斗并显示日志。

## 当前实现范围（V0）

- 单体后端 + 单页前端最小闭环
- 首次运行自动创建 SQLite 数据库并写入默认数据：
  - Player: `TestPlayer`
  - Character: `Knight`（100/100, Atk 20, Def 5）
  - Monster: `Slime`（50/50, Atk 8, Def 2）
  - Room: 默认绑定上述数据，状态 `Idle`
- API：
  - `GET /api/rooms/{roomId}` 获取房间状态
  - `POST /api/battle/start` 发起一次战斗
  - `POST /api/battle/reset` 重置当前房间怪物血量
  - `POST /api/battle/heal` 为当前角色恢复 10 HP
- 战斗规则：
  - 角色死亡时拒绝战斗并返回日志
  - 玩家与怪物 HP 都会在房间内持续保存
  - 每次点击“开始战斗”会推进一次战斗
  - 怪物死亡后不会自动重置
  - 角色先手，怪物存活则反击
  - 角色与怪物 HP 持久化到数据库
- 前端页面展示：玩家、角色 HP、怪物 HP、房间状态、开始战斗按钮、战斗日志
- 提供最小辅助操作：重置战斗、恢复10HP

## 当前未实现（明确超范围）

- 多人联机、房间槽位系统、多角色编队
- Auto 自动战斗、10s CD、30s 准备阶段
- 快照恢复、副本通关状态
- 技能/Buff/Debuff
- SignalR、微服务、Redis、Hangfire
