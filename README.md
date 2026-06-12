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

## 当前实现范围（V0.5）

### 房间系统
- 支持动态创建和删除多个房间，不再依赖固定的默认房间
- 创建房间时可选择怪物类型（Slime / Goblin / Wolf），每种怪物有独立的数值
- 创建房间本质上是"创建房间容器并绑定选定怪物"
- 首页（房间列表页）为新的入口，只展示房间基础信息（房间 ID、怪物、状态）
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
- 新用户可通过注册接口创建，注册成功后会自动生成默认角色 `Knight`，并将该角色写入用户的持久化当前角色选择

### 用户 / 角色系统
- 一个用户可以拥有多个角色
- 用户有一个持久化的当前角色选择（`User.ActiveCharacterId`）
- 当前角色是“用户级默认角色”语义，用于决定后续默认操作使用哪个角色
- 房间成员角色是“房间上下文”语义，对应 `RoomMember.CharacterId`
- 这两套语义不同：切换当前角色后，未来默认操作会变，但已写入房间的成员角色不会被自动回写

### 当前角色规则（当前基线）
- 当前角色不再由“第一个角色”长期决定，而是由持久化的 `ActiveCharacterId` 决定
- 兼容旧数据时，如果 `ActiveCharacterId` 为空或指向无效角色，系统会回退到该用户 `Id` 最小的角色
- 回退成功后，系统会自动补写 `ActiveCharacterId`
- `GET /api/user/character`、`GET /api/user/characters`、顶部角色栏高亮都基于这套解析后的当前角色规则

### 顶部多角色栏
- 房间列表页顶部角色栏展示当前登录用户的全部角色摘要
- 角色列表按角色 `Id` 升序展示，当前角色会高亮
- 用户可在该区域直接创建角色、删除符合约束的角色、切换当前角色

### 创建 / 删除 / 切换角色规则
- 创建角色：
  - 只需要输入角色名
  - 角色属性使用当前默认初始化规则：`HP 100/100`、`Attack 20`、`Defense 5`
  - 创建后不会自动切换当前角色
- 删除角色：
  - 不能删除最后一个角色
  - 不能删除仍被 `RoomMember` 引用的角色
  - 删除当前角色时，会自动切换到剩余角色中 `Id` 最小的角色
- 切换当前角色：
  - 只影响未来默认操作
  - 会立即影响 `GET /api/user/character`、顶部角色栏当前角色高亮、后续创建房间使用的角色、后续加入房间使用的角色
  - 不会影响既有 `RoomMember.CharacterId`、当前已加入房间中的成员角色身份、已存在战斗上下文、历史房间/战斗状态

### 房间与角色关系
- 创建房间 / 加入房间时，系统会解析当前激活角色，并将该角色写入 `RoomMember.CharacterId`
- 一旦角色以某个身份加入房间并写入 `RoomMember`，后续切换当前角色不会自动修改该房间成员记录
- 因此，“当前角色”和“房间成员角色”在一段时间内可能相同，也可能不同

### API
- `POST /api/user/register` 注册用户并返回 token（自动创建默认角色）
- `POST /api/user/login` 登录并返回 token
- `GET /api/user/characters` 需要携带登录 token 请求头，获取当前登录用户的全部角色摘要（按角色 Id 排序，并包含基于当前角色解析结果的 `isCurrent`）
- `POST /api/user/characters` 需要携带登录 token 请求头，仅输入角色名即可为当前用户创建新角色（使用默认初始化属性；创建后不会自动切换当前角色）
- `DELETE /api/user/characters/{characterId}` 需要携带登录 token 请求头，仅允许删除当前用户自己的角色；不能删除最后一个角色，也不能删除仍被房间成员引用的角色；若删除的是当前角色，会自动切换到剩余角色中 Id 最小的角色
- `GET /api/user/me` 使用 `Authorization: Bearer <token>` 请求头获取当前用户
- `GET /api/user/character` 使用 `Authorization: Bearer <token>` 请求头获取当前登录用户的当前角色（基于持久化的当前角色选择；若指针为空或无效，则回退到 Id 最小的角色并补写）
- `POST /api/user/character/select` 使用 `Authorization: Bearer <token>` 请求头切换当前登录用户的当前角色；该切换只影响未来默认操作，不会回写既有房间成员角色
- `POST /api/user/logout` 也需要 `Authorization: Bearer <token>`
- `GET /api/rooms` 获取所有房间列表（返回 `RoomSummaryResponse`，不含玩家/角色信息）
- `POST /api/rooms` 创建新房间（请求体含 `monsterType`，需要登录，使用当前激活角色创建房间成员，返回 `RoomDetailResponse`）
- `POST /api/rooms/{roomId}/join` 加入指定房间（需要登录，使用当前激活角色写入房间成员，返回 `RoomDetailResponse`）
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
- **房间列表页**（`/`、`/rooms`）：查看当前用户与顶部角色栏中的全部角色摘要（按当前角色规则高亮，可直接切换）；可通过顶部“+”卡片输入角色名创建角色，并可轻量删除符合约束的角色；查看所有房间（仅展示基础信息），选择怪物类型，创建/删除/进入房间
- **战斗页**（`/battle/{roomId}`）：对指定房间进行战斗操作，支持重置和治疗

## 当前未实现（明确超范围）

- 同一用户多个角色同时进入同一房间
- 房间内切换参战角色
- 战斗中换人
- 复杂角色管理（如职业、模板、属性分配）
- 角色编辑
- 多人加入房间、房间槽位系统、多角色编队
- Auto 自动战斗、CD、准备阶段
- 怪物模板系统
- 快照恢复、副本通关状态
- 技能/Buff/Debuff
- SignalR、微服务、Redis、Hangfire
