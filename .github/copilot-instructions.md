# Copilot Instructions

## 项目指南
- 项目开发约束：采用 .NET 8、ASP.NET Core Web API、Blazor WASM、EF Core + SQLite 三项目结构；战斗计算、数据逻辑和状态判定必须由服务端实现，前端只负责交互展示；遵循 YAGNI、扁平架构、增量迭代和最小依赖。
- V0.8 单用户队伍采用“点击准备即确认并自动结算”流程；不保留主控二次确认、取消准备或相关前端交互，留待 V0.9 多人确认实现。