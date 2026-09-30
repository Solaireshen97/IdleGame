# 单机内测：存档、发布与恢复

适用范围：一台 Windows 主机、一个游戏进程、SQLite 本地存档。本文是可复核的操作方案，不代表已经安装生产服务、配置备份计划或执行过部署。维护工具不会停止服务、切换配置或删除现有存档。

## 目录与服务配置

发布产物与玩家存档必须分开保存。示例目录可按机器实际位置调整，后续命令中的路径都应替换为真实绝对路径。

```text
E:\IdleGame\releases\20260930-a\     服务端和前端发布产物
E:\IdleGame\releases\20260929-a\     上一版发布产物
E:\IdleGame\data\game.db            当前玩家存档
E:\IdleGame\backups\                带时间和版本的独立备份
E:\IdleGame\restores\               恢复演练及回滚的新存档
E:\IdleGame\maintenance\            独立维护工具
```

当前服务器默认数据库路径相对于应用目录；正式采用上述目录前，应在服务的实际启动环境中设置 `ConnectionStrings__GameDb=Data Source=E:\IdleGame\data\game.db`。不要只在开发终端里设置环境变量就认为 Windows Service 已继承它。首次迁移现有存档路径时先停服，使用下面的备份/恢复命令生成新文件，再配置服务。保留原路径及其辅助文件，不直接拷贝运行中的 `.db`，也不只拷贝 `.db` 而忽略 WAL。

服务账号需要读写当前存档目录，备份账号需要读取源并写入备份目录。备份包含玩家数据、密码哈希和登录会话，应限制目录访问权限。存档用本地磁盘；这里的工具拒绝符号链接、目录 junction、Windows 设备路径和备用数据流。

## 构建与验证维护工具

在仓库根目录执行；维护项目不依赖服务器，单独构建不会触发游戏数据库初始化。

```powershell
dotnet test .\tools\Game.Maintenance\Tests\Game.Maintenance.Tests.csproj --configuration Release --artifacts-path .\artifacts\maintenance --nologo
dotnet publish .\tools\Game.Maintenance\Game.Maintenance.csproj --configuration Release --artifacts-path .\artifacts\maintenance --output .\artifacts\maintenance-publish --nologo
```

任一步骤退出码非零时停止，先解决错误。将 `maintenance-publish` 的完整内容放入维护工具目录；维护工具需要 .NET 10 Runtime。服务器当前使用 framework-dependent 发布，目标主机还需安装 **ASP.NET Core Runtime 10**，只有基础 .NET Runtime 不足以启动服务器。SQLite 原生运行库随完整发布目录一起分发，不只复制 DLL。

下面的辅助函数会在维护命令失败时停止后续步骤。使用固定发布的维护工具可避免运行维护时顺带构建业务项目。

```powershell
$tool = 'E:\IdleGame\maintenance\Game.Maintenance.dll'
function Invoke-Maintenance {
    param([string[]]$Arguments)
    & dotnet $tool @Arguments
    if ($LASTEXITCODE -ne 0) { throw "存档维护失败，停止后续操作。退出码：$LASTEXITCODE" }
}
```

## 日常一致备份

事先创建备份目录；工具要求目标父目录已存在，目标文件及其 `-wal`、`-shm`、`-journal` 辅助文件都不能存在。不提供覆盖参数。

```powershell
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss-fff'
$backup = "E:\IdleGame\backups\game-$stamp.db"
Invoke-Maintenance -Arguments @('backup', '--source', 'E:\IdleGame\data\game.db', '--target', $backup)
Invoke-Maintenance -Arguments @('verify', '--source', $backup)
```

`backup` 通过 SQLite 备份接口读取一致快照，包括 WAL 中已提交的记录，不包含未提交事务；允许游戏进程继续运行。成功前会检查数据库结构完整性和外键关系，转为无辅助文件的独立数据库，再将临时文件发布到目标路径。成功输出包含目标路径、字节数、SHA-256 与 UTC 检查时间；保留输出作为备份记录。锁冲突或校验失败会返回非零退出码，不能将失败当成已有可用备份。

`verify` 执行 `integrity_check` 和 `foreign_key_check`，不自动修复。它检查 SQLite 完整性，不证明游戏玩法、配表版本或奖励逻辑正确。对正在运行的数据库，主文件哈希不能代表 WAL 的完整逻辑状态，所以 `verify` 不输出哈希。

初期可每天一次、每次发布前再做一次备份，保留最近七天及每次发布前的备份；定期将成功备份复制到另一块磁盘或机器，避免单机磁盘故障同时丢失存档和备份。当前工具没有自动调度、自动清理或异地上传；保留策略由操作者执行。至少每周挑一份备份恢复到新路径，确认实际能够读取。

## 发布前后

1. 先构建并验证新的 Release 发布产物，放入新的版本目录；保留上一版目录。前端必须进入服务器的 `wwwroot`；仅发布服务器项目不会自动合并当前独立客户端。下面的发布冒烟检查会验证完整组合产物。
2. 记录当前发布版本、实际数据库路径、服务账号与启动参数。在 Windows 服务管理器停止游戏服务，确认状态已停止、旧游戏进程已退出。
3. 用维护工具将停服后的当前存档备份到新的带版本/时间路径，并运行 `verify`。维护命令失败时停止发布；仍保留原存档和上一版程序。
4. 将服务启动目标切到新的发布目录，保留存档的绝对连接路径；从 Windows 服务管理器启动服务。服务器启动会自动应用数据库迁移并初始化配表，不能视为纯粹换程序。
5. 检查启动日志、健康端点或明确的 API；实际登录测试账号，检查角色、房间状态与后台战斗/炼金推进。首页返回 200 不足以判断这些功能可用。
6. 有任何异常先停服；使用下一节恢复流程回到上一版程序和发布前的新恢复存档。不要让旧程序直接连接已被新版本迁移的数据库。

本次服务器与客户端必须成套发布。强化、兑换等经济命令现在带唯一 `RequestId`；旧缓存客户端缺少该字段时服务器会拒绝请求，更新后需要刷新客户端。不要长期保留旧前端连接新服务器。

本文不提供自动停服/发布脚本，避免在未确认实际服务名称、目录和权限前操作真实服务。仓库中有 `UseWindowsService` 并不证明服务已经注册。完成首次手动发布和恢复演练后，再根据实际拓扑增加自动化。

## 停服恢复与回滚

恢复前停止游戏服务并确认进程退出。`--service-stopped` 是操作者对该步骤的确认，工具无法从数据库文件推断某个实际 Windows Service 是否停止。恢复始终创建新目标文件，原存档与备份保留。

```powershell
$restoreStamp = Get-Date -Format 'yyyyMMdd-HHmmss-fff'
$restored = "E:\IdleGame\restores\game-restored-$restoreStamp.db"
Invoke-Maintenance -Arguments @('verify', '--source', 'E:\IdleGame\backups\game-before-release.db')
Invoke-Maintenance -Arguments @('restore', '--source', 'E:\IdleGame\backups\game-before-release.db', '--target', $restored, '--service-stopped')
Invoke-Maintenance -Arguments @('verify', '--source', $restored)
```

源应为工具生成的独立备份，不能存在 SQLite 辅助文件；有辅助文件的源应先通过 `backup` 生成独立快照。恢复后将服务连接字符串显式指向输出的新文件，确认服务账号具有读写权限。回滚时同时切回对应旧版本发布目录，再启动服务并验证玩家状态。恢复到过去快照会舍弃快照之后的游戏进度；失败后的当前存档仍保留在原路径供检查。

不要覆盖原 `.db`、删除原 WAL 或 SHM、在线恢复、在不清楚迁移是否兼容时仅回退程序。没有自动覆盖和删除选项就是为了使这些步骤可审查。

`20260930040000_AddEconomicRequestResults` 的回退有数据保护：只要收据中已有非空结果快照，`Down` 会拒绝丢弃结果字段。不要删除收据来绕过保护；回滚应使用升级前的完整备份与匹配的旧发布目录。维护工具不会替操作者执行迁移回退或生产部署。

## 临时存档演练覆盖

维护工具测试在独立随机临时目录创建数据库，覆盖在线 WAL 提交与未提交事务、恢复到新路径、哈希匹配、拒绝已有目标及全部辅助文件、拒绝缺失/空/损坏源、外键异常、相同源目标、相对路径、缺失父目录和参数错误。测试不接触玩家存档或实际 Windows 服务。

## 发布验证与 CI

在 Windows 主机和 PowerShell 7.2 或更新版本中运行：

```powershell
./tools/verify-server-release.ps1
# 可选：使用现有锁定 Playwright 依赖和已安装 Edge 检查真正 Blazor 启动
./tools/verify-server-release.ps1 -Browser
```

该入口将服务器和客户端各自发布为 Release，把客户端静态文件合入服务器发布目录，在随机本机端口启动独立子进程，数据库与发布文件分别保存在 `artifacts/server-release-smoke/<随机目录>/data` 和 `server`。只终止该入口启动的进程；保留临时数据库与日志供诊断，不停止实际服务，也不读取玩家存档。

检查覆盖 `/liveness`、`/readiness`、`/health`，实际发布的首页/CSS/脚本/.NET JavaScript/WASM响应与文件字节一致，注册、登录、未登录拒绝、创建角色和房间、准备后推进一个回合、退出登录后失效，以及后台扫描的成功完成时间继续前进。冒烟进程使用 Production 环境，前端的 API 地址只修改该次隔离发布副本；验证用明文 HTTP 只监听 `127.0.0.1`，不代表真实部署的 HTTPS 配置已通过验证。

`.github/workflows/server-validation.yml` 在 PR、main/master push 或人工触发时运行服务端回归、维护备份恢复测试、模拟器场景测试和上述发布冒烟；失败时保留冒烟日志七天。CI 使用 .NET 10 SDK 与 WebAssembly 构建工具。既有客户端战斗动画浏览器专项仍可通过 `tools/verify-combat.ps1 -BrowserOnly` 单独验证，不把整个服务端测试重复跑一遍。

默认 CI 运行 HTTP 模式。`-Browser` 需要按 `docs/combat-validation.md` 安装现有冻结依赖，并且本机已安装 Microsoft Edge；依赖缺失会明确失败。该模式打开实际发布的 `/login`，等待 Blazor 渲染真实登录表单，检查运行时错误、加载失败和错误遮罩，再保存截图及 JSON 记录。需要重用刚生成的发布副本时可加 `-PublishedRun '<上述 artifacts 下随机目录的绝对路径>'`；该参数跳过重编译，仍生成全新临时数据库和随机端口，不能用作新源码的发布验收。

此入口通过证明组合发布包与临时存档上的上述链路可用；不会执行生产部署、恢复 Windows 服务、自动备份调度或完整浏览器游玩验收。首次实际部署后应把真实目录、账号权限、HTTPS入口和一次恢复演练结果记入本机运维记录。
