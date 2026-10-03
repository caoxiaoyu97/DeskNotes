# 桌面便签

Windows 桌面任务便签。Markdown 是记录的正式存储格式，程序提供快捷添加、完成记录、历史回看和日报复制。

## 开发

需要 .NET 10 SDK（Windows）。

```powershell
dotnet build src/DeskNotes.App
dotnet run --project tests/DeskNotes.Tests
dotnet run --project src/DeskNotes.App
```

本地 SDK 可使用 `.\.dotnet\dotnet.exe` 替代 `dotnet`。

## 设计

- 数据默认位于 `%LOCALAPPDATA%\DeskNotes\data`，每月一个 Markdown 文件。
- 未完成任务持续显示；完成事项按实际完成日统计。
- Markdown 中的 HTML 注释保存任务编号与时间，请保留它们。
- 桌面模式尝试挂载到 Explorer 桌面窗口；不支持时退回普通非置顶窗口，并显示状态。
- Ctrl+Alt+N 快捷记录，托盘菜单可打开主窗口、数据目录和退出。

详细使用说明和验证范围将在可运行版本完成后补充。
