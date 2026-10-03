# 桌面便签

贴在桌面上的任务便签：随手记下要做的事，做完划掉，晚上直接复制出当天日报。所有记录以 Markdown 文件保存，不用这个程序也能直接打开查看。

## 快速开始

```powershell
.\build.ps1 -Run
```

第一步会运行存储测试，然后把程序打包到 `dist\DeskNotes.exe` 并启动。`dist` 目录可以直接复制到其他电脑运行，不需要另外安装 .NET。

开发时也可以直接运行：

```powershell
.\build.ps1 -SkipTests
.\dist\DeskNotes.exe
```

## 日常用法

在主窗口输入框写一句任务，按回车就保存。想到事情但正在别的软件里，按 `Ctrl+Alt+N` 弹出小窗口，写完回车保存，弹窗关闭。

点复选框完成任务，程序会记录实际完成时间。点任务文字可以编辑标题，也可以写一段“备注 / 实际成果”，这段内容会进入日报。左下角“复制日报”会打开当日日报，可以修改后复制成 Markdown。日期选择框用来回看以前某天，勾选“全部”忽略日期限制。

右上角 `···` 还有其他操作：回收区、打开 Markdown 文件夹、导出全部记录、切换记录目录、贴在桌面或切回普通窗口、调整字号、隐藏到托盘、退出。双击托盘图标等于快捷记录。

## 记录存在哪

默认目录是 `%LOCALAPPDATA%\DeskNotes\data`，每个月一个文件：

```
data/
├─ records/
│  └─ 2026-10.md
├─ backups/        自动保留的历史版本
└─ settings.json   窗口位置、字号等设置
```

Markdown 里每个任务长这样：

```markdown
## 2026-10-03
<!-- desknote:{"id":"...","created":"...","completed":null,"deleted":null,"isCompleted":false} -->
- [ ] 给 Git 文档工具加搜索
  先支持按标题搜索。
<!-- /desknote -->
```

标题、复选框和备注是普通 Markdown，可以直接手改；两个 HTML 注释保存编号和时间，请不要删除。直接手改文件后，程序会在两秒内自动刷新。想手动加任务，写一行 `- [ ] 内容` 也能被识别。

如果手动把 `- [ ]` 改成 `- [x]` 但没有填完成时间，程序会把它放进“完成日期待确认”，不会猜一个时间写进日报。

## 关于贴在桌面

便签保持普通窗口，但会固定到窗口层叠顺序的最底部，所以效果是：**普通程序都盖在它上面，桌面在它下面**。它不会抢焦点，程序启动时也不会打断你正在做的事。

早期版本尝试用 `SetParent` 把窗口挂成桌面窗口的子窗口，实测在 Windows 11 上这条路走不通：窗口报告“可见”但完全不绘制，而且子窗口拿不到键盘焦点，便签上打不了字。现在的做法不改父子关系，只固定层级，因此显示和输入都正常。

窗口被隐藏时（例如按了显示桌面）程序会自动把它重新显示出来。如果你不希望它常驻桌面，`···` 菜单里可以随时切换成普通窗口。

## 开发

```
src/DeskNotes.Core   任务模型、Markdown 读写、日报生成
src/DeskNotes.App    WPF 界面、桌面挂载、托盘、全局快捷键
tests/DeskNotes.Tests 存储与日报的结算测试
```

```powershell
.\.dotnet\dotnet.exe run --project tests\DeskNotes.Tests -c Release
```

界面自检（不写入正式记录，截图输出到 `work\preview.png`）：

```powershell
.\.dotnet\dotnet.exe run --project src\DeskNotes.App -c Release -- --preview
```
