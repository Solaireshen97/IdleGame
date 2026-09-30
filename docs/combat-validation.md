# 战斗验证

前提：.NET 10 SDK、PowerShell、Node.js 22.13 或更新版本、pnpm 11.19.0，以及可用浏览器。
最低 Node.js 版本由 pnpm 11.19.0 的 engines 要求决定（Playwright 本身要求 Node.js 20 或更新版本）。
浏览器测试的 Playwright 固定为 1.62.1（与本次验证机器已安装版本一致）；依赖和锁文件仅属于 `Game.Server.Tests/Client`。

首次在仓库根目录安装 JavaScript 依赖：

```powershell
pnpm --dir Game.Server.Tests/Client install --frozen-lockfile --ignore-scripts
```

Windows 默认使用已安装的 Microsoft Edge，无需下载浏览器。其他系统默认使用 Playwright Chromium；没有相应浏览器时，手动安装 Chromium 并显式选择：

```powershell
pnpm --dir Game.Server.Tests/Client exec playwright install chromium
./tools/verify-combat.ps1 -BrowserChannel chromium
```

完整验证依次运行服务端全部测试（包含逐回合确定性与服务重建验证）、平衡模拟器构建、客户端 Debug 构建，以及 3 项浏览器测试：

```powershell
./tools/verify-combat.ps1
```

只验证客户端（仍构建客户端生成测试需要的 scoped CSS）：

```powershell
./tools/verify-combat.ps1 -BrowserOnly
# -SkipServer 与 -BrowserOnly 等效
```

已完成客户端 Debug 构建时可加 `-SkipClientBuild`。CSS 有改动时必须重新构建。
也可直接运行 `pnpm --dir Game.Server.Tests/Client test`，但该命令不会构建 CSS。
`-BrowserChannel` 可覆盖 `PLAYWRIGHT_CHANNEL`；脚本结束后恢复原环境变量。

任一步骤失败都会停止后续步骤并返回非零退出码。入口不会自动安装依赖或浏览器，也不会启动游戏服务器；浏览器测试直接加载实际回放脚本及构建生成的 CSS。

事件反馈可靠性回归位于 `BattleFeedbackCoordinatorTests`、`BattleHistoryCapacityTests` 和 `BattleEventTests`：覆盖提交后事件晚到、重复轮询、版本倒退、服务重启后 ID 重用以及历史缓存淘汰。客户端保留一个前回合场景等待事实，关闭房间最多再获取三次；导航、禁用播放或跳过回合后丢弃待播场景。

当前历史采用单进程内存缓存，最多保留 256 个房间，最后发布后两小时过期；读取不会延长过期时间，清理在缓存访问时进行。每房间通常保留 80 条，但完整最新结算可超过此数量。进程重启不会恢复旧历史；日志以进程 epoch 区分重新计数的 ID，动画按房间、挑战和回合去重。数据库结果始终为权威状态。

逐回合行为基准和同种子模拟命令见 [平衡模拟器说明](../tools/Game.BalanceSimulator/README.md)。完整入口会构建该工具，确定性回归由服务端测试执行；手动模拟报告可加 `--trace` 检查首个不同回合。2026-09-30 本目标最终验收为 866 项服务端测试、3 项浏览器测试全部通过，模拟器与客户端构建通过，另已完成隔离数据库的真实启动检查。
