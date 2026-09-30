# Game.Maintenance

独立 .NET 10 SQLite 存档维护工具。完整操作说明见 [单机内测运维](../../docs/single-host-operations.md)。

```powershell
dotnet run --project .\tools\Game.Maintenance -- --help
dotnet test .\tools\Game.Maintenance\Tests\Game.Maintenance.Tests.csproj --configuration Release --artifacts-path .\artifacts\maintenance
```

命令要求绝对路径；目标父目录必须已存在，目标及其 SQLite 辅助文件不能存在：

```text
backup --source <现有存档绝对路径> --target <新备份绝对路径>
verify --source <现有存档或备份绝对路径>
restore --source <独立备份绝对路径> --target <新恢复存档绝对路径> --service-stopped
```

`backup` 支持在线 WAL 一致快照，输出前进行完整性和外键校验，并给出 SHA-256。`restore` 仅创建新文件；先人工停止服务，再传入确认标记，最后人工切换服务数据库路径。命令成功返回 0，失败返回 1。没有覆盖、删除旧存档、自动停服或自动迁移功能。
