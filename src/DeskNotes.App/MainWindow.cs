using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DeskNotes.Core;
using Forms = System.Windows.Forms;

namespace DeskNotes.App;

public sealed class MainWindow : Window
{
    private readonly AppSettings settings;
    private MarkdownStore store;
    private List<TaskItem> items = new();
    private readonly StackPanel list = new();
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Brushes.DimGray };
    private readonly TextBlock summary = new() { FontSize = 12, Foreground = Brushes.DimGray };
    private readonly TextBox input = new() { Padding = new Thickness(10), FontSize = 14, MaxLength = 1000 };
    private readonly TextBox search = new() { Padding = new Thickness(7), Width = 126, ToolTip = "搜索所有月份的任务和备注" };
    private readonly DatePicker date = new() { SelectedDate = DateTime.Today, Width = 132 };
    private readonly CheckBox all = new() { Content = "全部", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly bool preview;
    private readonly bool demo;
    private DesktopHost? desktop;
    private Forms.NotifyIcon? tray;
    private bool editing, closing, recycle;
    private string fingerprint = "";
    private DateTime lastDay = DateTime.Today;

    public MainWindow(bool preview, bool demo = false)
    {
        this.preview = preview;
        this.demo = demo;
        settings = preview || demo ? new AppSettings { DataDirectory = Path.Combine(Environment.CurrentDirectory, "work", "preview-data"), DesktopMode = false } : AppSettings.Load();
        store = new MarkdownStore(settings.DataDirectory);
        Title = "今日便签"; Width = Math.Clamp(settings.Width, 360, 900); Height = Math.Clamp(settings.Height, 440, 1200);
        Left = settings.Left; Top = settings.Top; MinWidth = 360; MinHeight = 440;
        if (!Forms.Screen.AllScreens.Any(s => s.WorkingArea.Contains((int)Left, (int)Top))) { Left = 60; Top = 80; }
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.CanResizeWithGrip; ShowInTaskbar = demo;
        // A desktop note should not steal focus from whatever you are doing when it
        // appears; you click it or use Ctrl+Alt+N when you actually want to write.
        ShowActivated = false;
        Background = Brush("#FAFBF6"); Foreground = Brush("#28342B"); FontFamily = new FontFamily("Microsoft YaHei UI"); FontSize = settings.TextSize;
        BuildUi();
        Loaded += (_, _) =>
        {
            if (preview || demo) SeedPreview();
            Reload();
            if (!preview)
            {
                desktop = new DesktopHost(this, QuickAdd);
                desktop.StatusChanged += message => Dispatcher.Invoke(() => status.Text = message);
                if (settings.DesktopMode) desktop.Attach();
                InitTray();
                status.Text = desktop.Status;
            }
            timer.Start();
            if (preview)
            {
                var capture = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                capture.Tick += (_, _) => { capture.Stop(); CapturePreview(); closing = true; Close(); Application.Current.Shutdown(); };
                capture.Start();
            }
        };
        timer.Tick += (_, _) =>
        {
            if (editing) return;
            if (DateTime.Today != lastDay) { if (date.SelectedDate?.Date == lastDay) date.SelectedDate = DateTime.Today; lastDay = DateTime.Today; Render(); }
            try { string current = Fingerprint(); if (current != fingerprint) Reload(); }
            catch (Exception e) { status.Text = "读取失败：" + e.Message; }
        };
        Closing += (_, e) => { if (!closing) { e.Cancel = true; Hide(); return; } if (!preview && !demo) SaveSettings(); };
        Closed += (_, _) => { timer.Stop(); desktop?.Dispose(); tray?.Dispose(); };
    }

    private static SolidColorBrush Brush(string value) => (SolidColorBrush)new BrushConverter().ConvertFromString(value)!;
    private static Button Button(string text, Action action) { var b = new Button { Content = text }; b.Click += (_, _) => action(); return b; }
    private void BuildUi()
    {
        var border = new Border { BorderBrush = Brush("#CAD4C5"), BorderThickness = new Thickness(1), Padding = new Thickness(20, 16, 20, 14) };
        Content = border;
        var root = new Grid(); border.Child = root;
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto }) root.RowDefinitions.Add(new RowDefinition { Height = height });
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 8), Background = Brushes.Transparent };
        var menu = Button("···", ShowMenu); menu.VerticalAlignment = VerticalAlignment.Top; DockPanel.SetDock(menu, Dock.Right); header.Children.Add(menu);
        var heading = new StackPanel();
        heading.Children.Add(new TextBlock { Text = "今日便签", FontSize = 25, FontWeight = FontWeights.SemiBold });
        heading.Children.Add(new TextBlock { Text = "把事情记下，把今天过好。", FontSize = 11, Foreground = Brush("#7B8778"), Margin = new Thickness(0, 4, 0, 5) });
        heading.MouseLeftButtonDown += (_, e) => { if (e.LeftButton == MouseButtonState.Pressed) { if (desktop != null) desktop.BeginDrag(); else DragMove(); } };
        header.Children.Add(heading); root.Children.Add(header);
        var entry = new DockPanel { Margin = new Thickness(0, 8, 0, 12) };
        var add = Button("＋", AddInput); add.Background = Brush("#DCE8D3"); DockPanel.SetDock(add, Dock.Right); entry.Children.Add(add);
        input.ToolTip = "输入任务，回车保存 · Ctrl+Alt+N 随时记录";
        input.KeyDown += (_, e) => { if (e.Key == Key.Enter) { AddInput(); e.Handled = true; } };
        entry.Children.Add(WithPlaceholder(input, "＋ 记点什么，回车添加…")); Grid.SetRow(entry, 1); root.Children.Add(entry);
        var filters = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        search.TextChanged += (_, _) => Render(); var searchBox = WithPlaceholder(search, "搜索"); DockPanel.SetDock(searchBox, Dock.Right); filters.Children.Add(searchBox);
        filters.Children.Add(date); filters.Children.Add(all);
        date.SelectedDateChanged += (_, _) => Render(); all.Checked += (_, _) => Render(); all.Unchecked += (_, _) => Render();
        Grid.SetRow(filters, 2); root.Children.Add(filters);
        summary.Margin = new Thickness(0, 0, 0, 10); Grid.SetRow(summary, 3); root.Children.Add(summary);
        var scroll = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(scroll, 4); root.Children.Add(scroll);
        var footer = new DockPanel { Margin = new Thickness(0, 12, 0, 10) };
        var report = Button("复制日报 ↗", ShowReport); report.Background = Brush("#DCE8D3"); DockPanel.SetDock(report, Dock.Right); footer.Children.Add(report);
        var today = Button("回到今天", () => { recycle = false; all.IsChecked = false; search.Clear(); date.SelectedDate = DateTime.Today; Render(); });
        today.Background = Brushes.Transparent; footer.Children.Add(today); Grid.SetRow(footer, 5); root.Children.Add(footer);
        Grid.SetRow(status, 6); root.Children.Add(status);
    }

    private static Grid WithPlaceholder(TextBox box, string text)
    {
        var grid = new Grid(); grid.Children.Add(box);
        var hint = new TextBlock { Text = text, Foreground = Brush("#8C9785"), FontSize = 12, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        grid.Children.Add(hint);
        void Update() => hint.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        box.TextChanged += (_, _) => Update(); Update(); return grid;
    }

    private void AddInput()
    {
        if (string.IsNullOrWhiteSpace(input.Text)) return;
        if (Save(new TaskItem { Title = input.Text.Trim(), CreatedAt = DateTimeOffset.Now })) input.Clear();
    }
    private bool Save(TaskItem task)
    {
        try { store.Save(task); Reload(); status.Text = "已保存到 Markdown · " + DateTime.Now.ToString("HH:mm:ss"); return true; }
        catch (Exception e) { status.Text = "未保存：" + e.Message; MessageBox.Show(this, e.Message + "\n\n输入内容仍保留，请先复制再处理文件冲突。", "保存未完成", MessageBoxButton.OK, MessageBoxImage.Warning); return false; }
    }
    private void Reload()
    {
        try { items = store.Load(); fingerprint = Fingerprint(); Render(); }
        catch (Exception e) { status.Text = "读取失败，保留当前显示：" + e.Message; }
    }
    private string Fingerprint()
    {
        string folder = Path.Combine(settings.DataDirectory, "records");
        return Directory.Exists(folder) ? string.Join("|", Directory.EnumerateFiles(folder, "*.md").OrderBy(x => x).Select(x => { var f = new FileInfo(x); return f.Name + f.Length + f.LastWriteTimeUtc.Ticks; })) : "";
    }
    private static TaskItem Copy(TaskItem t) => new() { Id = t.Id, Title = t.Title, Notes = t.Notes, CreatedAt = t.CreatedAt, IsCompleted = t.IsCompleted, CompletedAt = t.CompletedAt, DeletedAt = t.DeletedAt };
    private void Render()
    {
        list.Children.Clear();
        DateTime day = date.SelectedDate?.Date ?? DateTime.Today;
        bool global = all.IsChecked == true || !string.IsNullOrWhiteSpace(search.Text);
        var visible = items.Where(t => recycle ? t.DeletedAt != null : t.DeletedAt == null)
            .Where(t => string.IsNullOrWhiteSpace(search.Text) || (t.Title + "\n" + t.Notes).Contains(search.Text, StringComparison.OrdinalIgnoreCase)).ToList();
        if (recycle) { summary.Text = "回收区 · " + visible.Count + " 项，可恢复"; foreach (var t in visible) AddCard(t); return; }
        var pending = visible.Where(t => !t.IsCompleted && (global || t.CreatedAt.LocalDateTime.Date <= day)).OrderByDescending(t => t.CreatedAt).ToList();
        var complete = visible.Where(t => t.IsCompleted && (global || t.CompletedAt?.LocalDateTime.Date == day)).OrderByDescending(t => t.CompletedAt).ToList();
        var unknown = visible.Where(t => t.IsCompleted && t.CompletedAt == null && !global).ToList();
        summary.Text = (global ? "全部记录" : day.ToString("M月d日")) + $"  ·  {pending.Count} 待办 / {complete.Count} 完成";
        Section("待办", pending);
        Section("已完成", complete);
        if (unknown.Count > 0) Section("完成日期待确认", unknown);
        if (pending.Count + complete.Count + unknown.Count == 0)
            list.Children.Add(new TextBlock { Text = "给今天留一点空间。\n想到什么，就在上面记下来。", Foreground = Brush("#899482"), Margin = new Thickness(6, 38, 6, 0), TextWrapping = TextWrapping.Wrap, LineHeight = 25 });
    }
    private void Section(string title, List<TaskItem> tasks)
    {
        if (tasks.Count == 0) return;
        list.Children.Add(new TextBlock { Text = title + "  " + tasks.Count, FontWeight = FontWeights.SemiBold, Foreground = Brush("#617155"), Margin = new Thickness(0, 8, 0, 8), FontSize = 12 });
        foreach (var t in tasks) AddCard(t);
    }
    private void AddCard(TaskItem t)
    {
        var card = new Border { Background = t.IsCompleted ? Brush("#F0F3EA") : Brushes.White, Padding = new Thickness(10), Margin = new Thickness(0, 0, 0, 7), CornerRadius = new CornerRadius(6) };
        var dock = new DockPanel(); card.Child = dock;
        if (recycle) { var restore = Button("恢复", () => { var c = Copy(t); c.DeletedAt = null; Save(c); }); DockPanel.SetDock(restore, Dock.Right); dock.Children.Add(restore); }
        else
        {
            var check = new CheckBox { IsChecked = t.IsCompleted, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 5, 9, 0) };
            check.Click += (_, _) => { var c = Copy(t); c.IsCompleted = check.IsChecked == true; c.CompletedAt = c.IsCompleted ? DateTimeOffset.Now : null; if (!Save(c)) check.IsChecked = t.IsCompleted; };
            DockPanel.SetDock(check, Dock.Left); dock.Children.Add(check);
        }
        var body = new StackPanel { Cursor = Cursors.Hand, Background = Brushes.Transparent };
        body.Children.Add(new TextBlock { Text = t.Title, TextWrapping = TextWrapping.Wrap, Foreground = t.IsCompleted ? Brush("#74816A") : Foreground, TextDecorations = t.IsCompleted ? TextDecorations.Strikethrough : null });
        if (!string.IsNullOrWhiteSpace(t.Notes)) body.Children.Add(new TextBlock { Text = t.Notes, FontSize = 12, Foreground = Brush("#7B8778"), TextWrapping = TextWrapping.Wrap, MaxHeight = 54, Margin = new Thickness(0, 5, 0, 0) });
        string label = t.IsCompleted ? (t.CompletedAt?.LocalDateTime.ToString("MM-dd HH:mm 完成") ?? "完成日期未知 · 右键确认") : t.CreatedAt.LocalDateTime.Date < DateTime.Today ? t.CreatedAt.LocalDateTime.ToString("MM-dd 遗留") : "";
        if (label != "") body.Children.Add(new TextBlock { Text = label, FontSize = 10, Foreground = Brush("#94A08C"), Margin = new Thickness(0, 5, 0, 0) });
        body.MouseLeftButtonUp += (_, _) => { if (!recycle) Edit(t, false); };
        dock.Children.Add(body);
        var context = new ContextMenu();
        AddMenu(context, "编辑", () => Edit(t, false));
        if (t.IsCompleted) AddMenu(context, "完成日期设为当前选中日", () => { var c = Copy(t); c.CompletedAt = new DateTimeOffset((date.SelectedDate ?? DateTime.Today).Date.AddHours(12)); Save(c); });
        AddMenu(context, recycle ? "恢复任务" : "移入回收区", () => { var c = Copy(t); c.DeletedAt = recycle ? null : DateTimeOffset.Now; Save(c); });
        card.ContextMenu = context; list.Children.Add(card);
    }
    private void Edit(TaskItem t, bool quick)
    {
        if (editing) return;
        editing = true;
        var dialog = new EditWindow(t, quick, Save);
        try { dialog.ShowDialog(); }
        finally { editing = false; Reload(); }
    }
    private void QuickAdd() => Edit(new TaskItem { CreatedAt = DateTimeOffset.Now }, true);
    private static void AddMenu(ContextMenu menu, string text, Action action) { var item = new MenuItem { Header = text }; item.Click += (_, _) => action(); menu.Items.Add(item); }
    private void ShowMenu()
    {
        var menu = new ContextMenu();
        AddMenu(menu, "随手记录  Ctrl+Alt+N", QuickAdd);
        AddMenu(menu, "补记已完成事项", () => Edit(new TaskItem { CreatedAt = DateTimeOffset.Now, IsCompleted = true, CompletedAt = DateTimeOffset.Now }, false));
        AddMenu(menu, recycle ? "返回任务" : "回收区", () => { recycle = !recycle; Render(); });
        AddMenu(menu, "打开 Markdown 文件夹", OpenFolder);
        AddMenu(menu, "导出全部记录为 Markdown", Export);
        AddMenu(menu, "选择数据文件夹…", ChooseFolder);
        AddMenu(menu, settings.DesktopMode ? "切换为普通窗口" : "贴在桌面", () => { settings.DesktopMode = !settings.DesktopMode; if (settings.DesktopMode) desktop?.Attach(); else desktop?.Detach(); SaveSettings(); });
        AddMenu(menu, "字号 " + settings.TextSize + " → " + (settings.TextSize >= 18 ? 12 : settings.TextSize + 2), () => { settings.TextSize = settings.TextSize >= 18 ? 12 : settings.TextSize + 2; FontSize = settings.TextSize; SaveSettings(); });
        AddMenu(menu, "隐藏到托盘", Hide);
        AddMenu(menu, "退出", Quit);
        menu.IsOpen = true;
    }
    private void ShowReport()
    {
        var window = new Window { Title = "今日日报 · 可编辑后复制", Width = 560, Height = 580, WindowStartupLocation = WindowStartupLocation.CenterScreen, FontFamily = FontFamily, FontSize = 14, Background = Background };
        var panel = new DockPanel { Margin = new Thickness(18) }; window.Content = panel;
        var text = new TextBox { Text = DailyReport.Build(items, DateOnly.FromDateTime(date.SelectedDate ?? DateTime.Today)), AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(12) };
        var copy = Button("复制 Markdown 日报", () => { try { Clipboard.SetText(text.Text); status.Text = "日报已复制"; window.Close(); } catch (Exception e) { MessageBox.Show(e.Message, "复制失败"); } });
        copy.Margin = new Thickness(0, 12, 0, 0); DockPanel.SetDock(copy, Dock.Bottom); panel.Children.Add(copy); panel.Children.Add(text); window.Show();
    }
    private void InitTray()
    {
        tray = new Forms.NotifyIcon { Text = "桌面便签 · Ctrl+Alt+N 随手记", Icon = System.Drawing.SystemIcons.Information, Visible = true };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("显示便签", null, (_, _) => Dispatcher.Invoke(() => { Show(); if (settings.DesktopMode) desktop?.Attach(); else Activate(); }));
        menu.Items.Add("快速记录", null, (_, _) => Dispatcher.Invoke(QuickAdd));
        menu.Items.Add("打开记录目录", null, (_, _) => Dispatcher.Invoke(OpenFolder));
        menu.Items.Add("退出", null, (_, _) => Dispatcher.Invoke(Quit));
        tray.ContextMenuStrip = menu; tray.DoubleClick += (_, _) => Dispatcher.Invoke(QuickAdd);
    }
    private void OpenFolder() { Directory.CreateDirectory(settings.DataDirectory); Process.Start(new ProcessStartInfo(settings.DataDirectory) { UseShellExecute = true }); }
    private void ChooseFolder()
    {
        if (editing) { status.Text = "请先保存或关闭编辑窗口，再切换目录。"; return; }
        using var dialog = new Forms.FolderBrowserDialog { Description = "选择记录目录（切换到该目录，原目录记录不会搬移）", SelectedPath = settings.DataDirectory, UseDescriptionForTitle = true };
        if (dialog.ShowDialog() != Forms.DialogResult.OK) return;
        var candidate = new MarkdownStore(dialog.SelectedPath);
        try { var loaded = candidate.Load(); store = candidate; settings.DataDirectory = dialog.SelectedPath; items = loaded; SaveSettings(); Reload(); status.Text = "已切换记录目录"; }
        catch (Exception e) { MessageBox.Show(e.Message, "无法打开目录"); }
    }
    private void Export()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { FileName = "全部便签-" + DateTime.Now.ToString("yyyyMMdd") + ".md", Filter = "Markdown|*.md" };
        if (dialog.ShowDialog() != true) return;
        string records = Path.GetFullPath(Path.Combine(settings.DataDirectory, "records")) + Path.DirectorySeparatorChar;
        if (Path.GetFullPath(dialog.FileName).StartsWith(records, StringComparison.OrdinalIgnoreCase)) { MessageBox.Show("请将汇总导出到 records 之外，避免再次读入。", "选择其他位置"); return; }
        var text = new StringBuilder("# 全部便签\n\n");
        foreach (var group in items.Where(t => t.DeletedAt == null).OrderBy(t => t.CreatedAt).GroupBy(t => t.CreatedAt.LocalDateTime.Date))
        {
            text.AppendLine("## " + group.Key.ToString("yyyy-MM-dd"));
            foreach (var t in group) { text.AppendLine($"- [{(t.IsCompleted ? "x" : " ")}] {t.Title}"); if (t.Notes != "") foreach (var line in t.Notes.Split('\n')) text.AppendLine("  " + line); if (t.CompletedAt != null) text.AppendLine("  完成：" + t.CompletedAt.Value.LocalDateTime.ToString("yyyy-MM-dd HH:mm")); }
            text.AppendLine();
        }
        File.WriteAllText(dialog.FileName, text.ToString(), new UTF8Encoding(false)); status.Text = "汇总已导出";
    }
    private void SaveSettings()
    {
        if (preview || demo) return;
        settings.Left = Left; settings.Top = Top; settings.Width = ActualWidth; settings.Height = ActualHeight;
        settings.Save();
    }
    private void Quit() { closing = true; Close(); Application.Current.Shutdown(); }
    private void SeedPreview()
    {
        if (store.Load().Count > 0) return;
        foreach (var t in new[] {
            new TaskItem { Title = "给 Git 文档工具加搜索", CreatedAt = DateTimeOffset.Now, Notes = "先支持按标题搜索，让查找更顺手。" },
            new TaskItem { Title = "整理部署说明", CreatedAt = DateTimeOffset.Now.AddDays(-1) },
            new TaskItem { Title = "修复文档导出乱码", CreatedAt = DateTimeOffset.Now, IsCompleted = true, CompletedAt = DateTimeOffset.Now, Notes = "已验证中文导出正常。" },
            new TaskItem { Title = "调整首页布局", CreatedAt = DateTimeOffset.Now, IsCompleted = true, CompletedAt = DateTimeOffset.Now } }) store.Save(t);
    }
    private void CapturePreview()
    {
        var image = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32); image.Render(this);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image));
        string path = Path.Combine(Environment.CurrentDirectory, "work", "preview.png"); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var file = File.Create(path); png.Save(file);
    }
}
