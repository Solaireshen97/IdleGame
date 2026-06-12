# IdleGame

最基础可运行版本的网页放置类自动战斗游戏骨架，基于 .NET 8、ASP.NET Core Web API、Blazor WebAssembly、EF Core + SQLite。

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
4. 打开前端页面后进入房间列表，点击"创建房间"，再点击"进入战斗"进行游戏。

## 当前实现范围（V0.4）

### 房间系统
- 支持动态创建和删除多个房间，不再依赖固定的默认房间
- 创建房间时可选择怪物类型（Slime / Goblin / Wolf），每种怪物有独立的数值
- 创建房间本质上是"创建房间容器并绑定选定怪物"，不暴露玩家/角色信息
- 首页（房间列表页）为新的入口，只展示房间基础信息（房间ID、怪物、状态）
- 每个房间绑定一个独立的怪物实例，拥有独立的 HP 和战斗进度
- 不同房间的怪物 HP 互不影响

### 怪物选项
| 怪物   | HP | MaxHP | Attack | Defense |
|--------|----|-------|--------|---------|
| Slime  | 50 | 50    | 8      | 2       |
| Goblin | 80 | 80    | 12     | 4       |
| Wolf   | 65 | 65    | 15     | 3       |

### 初始化数据
- 首次运行自动创建 SQLite 数据库
- 当前不再自动创建默认用户、默认角色或默认房间
- 新用户可通过注册接口创建，注册成功后会自动生成默认角色 `Knight`

### API
- `POST /api/user/register` 注册用户并返回 token（自动创建默认角色）
- `POST /api/user/login` 登录并返回 token
- `GET /api/user/characters` 需要携带登录 token 请求头，获取当前登录用户的全部角色摘要（按角色 Id 排序）
- `GET /api/user/me` 使用 `Authorization: Bearer <token>` 请求头获取当前用户
- `GET /api/user/character` 使用 `Authorization: Bearer <token>` 请求头获取当前登录用户的当前角色（当前规则为第一个角色）
- `POST /api/user/logout` 也需要 `Authorization: Bearer <token>`
- `GET /api/rooms` 获取所有房间列表（返回 `RoomSummaryResponse`，不含玩家/角色信息）
- `POST /api/rooms` 创建新房间（请求体含 `monsterType`，返回 `RoomSummaryResponse`）
- `GET /api/rooms/{roomId}` 获取指定房间完整状态（含玩家/角色信息，供战斗页使用）
- `DELETE /api/rooms/{roomId}` 删除指定房间（仅限 Idle 状态）
- `POST /api/battle/start` 需要登录后调用，推进当前用户在该房间中的一次战斗（基于 roomId）
- `POST /api/battle/reset` 需要登录后调用，重置当前房间怪物血量
- `POST /api/battle/heal` 需要登录后调用，为当前用户在该房间中的角色恢复 10 HP

#### 创建房间请求示例
```json
POST /api/rooms
{
  "monsterType": "Goblin"
}
```

### 战斗规则
- 角色死亡时拒绝战斗并返回日志
- 玩家与怪物 HP 都会在房间内持续保存
- 每次点击"开始战斗"会推进一次战斗
- 怪物死亡后不会自动重置，需手动点击"重置战斗"
- 角色先手，怪物存活则反击
- 角色与怪物 HP 持久化到数据库

### 前端页面
- **房间列表页**（`/`、`/rooms`）：查看当前用户与顶部角色栏中的全部角色摘要（当前角色为首个角色且仅做只读展示），查看所有房间（仅展示基础信息），选择怪物类型，创建/删除/进入房间
- **战斗页**（`/battle/{roomId}`）：对指定房间进行战斗操作，支持重置和治疗

## 当前未实现（明确超范围）

- 多人加入房间、房间槽位系统、多角色编队
- Auto 自动战斗、CD、准备阶段
- 怪物模板系统
- 快照恢复、副本通关状态
- 技能/Buff/Debuff
- SignalR、微服务、Redis、Hangfire
