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
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DeskNotes.Core;
using Forms = System.Windows.Forms;

namespace DeskNotes.App;

public sealed class MainWindow : Window
{
    private const int ModePending = 0, ModeDone = 1, ModeAll = 2;

    private readonly AppSettings settings;
    private MarkdownStore store;
    private List<TaskItem> items = new();
    private readonly StackPanel list = new();
    private readonly Border frame = new();
    private readonly TextBlock summary = new() { FontSize = 12, Foreground = new SolidColorBrush(Color.FromArgb(225, 74, 92, 68)) };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = new SolidColorBrush(Color.FromArgb(185, 70, 84, 66)) };
    private readonly TextBox input = new() { Padding = new Thickness(10), FontSize = 14, MaxLength = 1000, BorderThickness = new Thickness(0), Background = Glass(165, 255, 255, 255) };
    private readonly TextBox search = new() { Padding = new Thickness(7), Width = 88, BorderThickness = new Thickness(0), Background = Glass(140, 255, 255, 255), ToolTip = "搜索所有任务和备注" };
    private readonly List<Button> tabs = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly bool preview;
    private readonly bool demo;
    private DesktopHost? desktop;
    private Forms.NotifyIcon? tray;
    private bool editing, closing, recycle;
    private int mode = ModePending;
    private string fingerprint = "";
    private DateTime lastDay = DateTime.Today;

    public MainWindow(bool preview, bool demo = false, int startMode = 0)
    {
        this.preview = preview;
        this.demo = demo;
        settings = preview || demo ? new AppSettings { DataDirectory = Path.Combine(Environment.CurrentDirectory, "work", "preview-data"), DesktopMode = false } : AppSettings.LoadOrMigrate();
        Directory.CreateDirectory(settings.DataDirectory);
        store = new MarkdownStore(settings.DataDirectory);
        Title = "今日便签"; Width = Math.Clamp(settings.Width, 340, 900); Height = Math.Clamp(settings.Height, 420, 1200);
        Left = settings.Left; Top = settings.Top; MinWidth = 340; MinHeight = 420;
        if (!Forms.Screen.AllScreens.Any(s => s.WorkingArea.Contains((int)Left, (int)Top))) { Left = 60; Top = 80; }
        // A translucent panel needs a layered window; the shadow and rounded corners
        // are drawn by us, so WPF chrome and the system resize border are off.
        WindowStyle = WindowStyle.None; AllowsTransparency = true; ResizeMode = ResizeMode.NoResize;
        Background = Brushes.Transparent; Foreground = new SolidColorBrush(Color.FromRgb(40, 52, 43));
        FontFamily = new FontFamily("Microsoft YaHei UI"); FontSize = settings.TextSize;
        ShowInTaskbar = demo;
        // A desktop note should not steal focus from whatever you are doing when it
        // appears; you click it or use Ctrl+Alt+N when you actually want to write.
        ShowActivated = false;
        BuildUi();
        SetMode(startMode);
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
                status.Text = AppSettings.StartupNote.Length > 0 ? AppSettings.StartupNote + " · " + desktop.Status : desktop.Status;
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
            if (DateTime.Today != lastDay) { lastDay = DateTime.Today; Render(); }
            try { string current = Fingerprint(); if (current != fingerprint) Reload(); }
            catch (Exception e) { status.Text = "读取失败：" + e.Message; }
        };
        Closing += (_, e) => { if (!closing) { e.Cancel = true; Hide(); return; } if (!preview && !demo) SaveSettings(); };
        Closed += (_, _) => { timer.Stop(); desktop?.Dispose(); tray?.Dispose(); };
    }

    private static SolidColorBrush Brush(string value) => (SolidColorBrush)new BrushConverter().ConvertFromString(value)!;
    private static SolidColorBrush Glass(byte alpha, byte r, byte g, byte b) => new(Color.FromArgb(alpha, r, g, b));
    private static Button Button(string text, Action action) { var b = new Button { Content = text }; b.Click += (_, _) => action(); return b; }

    private SolidColorBrush PanelBrush()
    {
        byte alpha = (byte)Math.Round(Math.Clamp(settings.PanelAlpha, 0.4, 1.0) * 255);
        return new SolidColorBrush(Color.FromArgb(alpha, 250, 251, 246));
    }

    private void ApplyPanelLook()
    {
        frame.Background = PanelBrush();
        frame.BorderBrush = Glass(90, 255, 255, 255);
    }

    private void BuildUi()
    {
        frame.Padding = new Thickness(18, 14, 18, 12);
        frame.CornerRadius = new CornerRadius(10);
        frame.BorderThickness = new Thickness(1);
        frame.Effect = new DropShadowEffect { BlurRadius = 16, ShadowDepth = 0, Opacity = 0.18, Color = Colors.Black };
        ApplyPanelLook();

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        frame.Child = root;

        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 8), Background = Brushes.Transparent };
        var menu = Button("···", ShowMenu); menu.VerticalAlignment = VerticalAlignment.Top; menu.Background = Brushes.Transparent; DockPanel.SetDock(menu, Dock.Right); header.Children.Add(menu);
        var heading = new StackPanel();
        heading.Children.Add(new TextBlock { Text = "今日便签", FontSize = 24, FontWeight = FontWeights.SemiBold });
        heading.Children.Add(new TextBlock { Text = "没做完的会一直留着。", FontSize = 11, Foreground = new SolidColorBrush(Color.FromArgb(200, 100, 116, 96)), Margin = new Thickness(0, 3, 0, 4) });
        heading.MouseLeftButtonDown += (_, e) =>
        {
            if (e.LeftButton != MouseButtonState.Pressed) return;
            if (desktop != null) desktop.BeginDrag(); else DragMove();
            // Persist where the user dropped the note, so it comes back there next time.
            SaveSettings();
        };
        header.Children.Add(heading); root.Children.Add(header);

        var entry = new DockPanel { Margin = new Thickness(0, 8, 0, 10) };
        var add = Button("＋", AddInput); add.Background = Glass(180, 219, 232, 209); add.BorderThickness = new Thickness(0); DockPanel.SetDock(add, Dock.Right); entry.Children.Add(add);
        input.ToolTip = "输入任务，回车保存 · Ctrl+Alt+N 随时记录";
        input.KeyDown += (_, e) => { if (e.Key == Key.Enter) { AddInput(); e.Handled = true; } };
        entry.Children.Add(WithPlaceholder(input, "＋ 记点什么，回车添加…")); Grid.SetRow(entry, 1); root.Children.Add(entry);

        var tabBar = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        search.TextChanged += (_, _) => Render();
        var searchBox = WithPlaceholder(search, "搜索"); DockPanel.SetDock(searchBox, Dock.Right); tabBar.Children.Add(searchBox);
        var tabHost = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (label, value) in new (string, int)[] { ("待办", ModePending), ("完成", ModeDone), ("全部", ModeAll) })
        {
            int captured = value;
            var tab = new Button { Content = label, Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(12, 5, 12, 5), FontSize = 12, BorderThickness = new Thickness(0) };
            tab.Click += (_, _) => SetMode(captured);
            tabs.Add(tab); tabHost.Children.Add(tab);
        }
        tabBar.Children.Add(tabHost); Grid.SetRow(tabBar, 2); root.Children.Add(tabBar);

        summary.Margin = new Thickness(0, 0, 0, 8); Grid.SetRow(summary, 3); root.Children.Add(summary);

        var scroll = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(scroll, 4); root.Children.Add(scroll);

        var footer = new DockPanel { Margin = new Thickness(0, 10, 0, 8) };
        // The resize handle is docked first so it keeps the corner and the button
        // never covers it; the system resize border is gone with AllowsTransparency.
        var grip = new TextBlock
        {
            Text = "◢", FontSize = 12, Foreground = Glass(170, 90, 106, 86),
            VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(4, 0, 0, 2),
            Cursor = Cursors.SizeNWSE, ToolTip = "拖动调整大小"
        };
        grip.MouseLeftButtonDown += (_, _) => ResizeFromGrip();
        DockPanel.SetDock(grip, Dock.Right); footer.Children.Add(grip);
        var report = Button("复制今天日报 ↗", () => ShowReport(DateOnly.FromDateTime(DateTime.Today)));
        report.Background = Glass(180, 219, 232, 209); report.BorderThickness = new Thickness(0); DockPanel.SetDock(report, Dock.Right); footer.Children.Add(report);
        Grid.SetRow(footer, 5); root.Children.Add(footer);

        Grid.SetRow(status, 6); root.Children.Add(status);

        Content = frame;
    }

    private static Grid WithPlaceholder(TextBox box, string text)
    {
        var grid = new Grid(); grid.Children.Add(box);
        var hint = new TextBlock { Text = text, Foreground = Glass(190, 110, 126, 104), FontSize = 12, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        grid.Children.Add(hint);
        void Update() => hint.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        box.TextChanged += (_, _) => Update(); Update(); return grid;
    }

    private void SetMode(int value)
    {
        recycle = false;
        mode = value;
        for (int i = 0; i < tabs.Count; i++)
            tabs[i].Background = i == mode ? Glass(190, 219, 232, 209) : Brushes.Transparent;
        Render();
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
        bool searching = !string.IsNullOrWhiteSpace(search.Text);
        var visible = items
            .Where(t => recycle ? t.DeletedAt != null : t.DeletedAt == null)
            .Where(t => !searching || (t.Title + "\n" + t.Notes).Contains(search.Text, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (recycle)
        {
            summary.Text = "回收区 · " + visible.Count + " 项（右键可恢复）";
            foreach (var t in visible.OrderByDescending(t => t.DeletedAt)) AddCard(t);
            if (visible.Count == 0) Empty("回收区是空的。");
            return;
        }

        var pending = visible.Where(t => !t.IsCompleted).OrderBy(t => t.CreatedAt).ToList();
        var done = visible.Where(t => t.IsCompleted).ToList();
        var undated = done.Where(t => t.CompletedAt == null).OrderBy(t => t.CreatedAt).ToList();
        var dated = done.Where(t => t.CompletedAt != null).ToList();
        int carried = pending.Count(t => t.CreatedAt.LocalDateTime.Date < DateTime.Today);

        if (mode == ModePending)
        {
            summary.Text = pending.Count == 0 ? "没有未完成的事"
                : carried > 0 ? $"待办 {pending.Count} 项 · 其中 {carried} 项是之前留下的"
                : $"待办 {pending.Count} 项";
            Section("待办", pending);
            if (pending.Count == 0) Empty("目前没有未完成的事。\n想到什么，就在上面记下来。");
            return;
        }

        if (mode == ModeDone)
        {
            int days = dated.Select(t => t.CompletedAt!.Value.LocalDateTime.Date).Distinct().Count();
            summary.Text = done.Count == 0 ? "还没有完成记录" : $"完成 {done.Count} 项 · 分布在这 {days} 天";
            foreach (var group in dated.GroupBy(t => t.CompletedAt!.Value.LocalDateTime.Date).OrderByDescending(g => g.Key))
                DaySection(group.Key, group.OrderByDescending(t => t.CompletedAt).ToList());
            Section("完成日期待确认", undated);
            if (done.Count == 0) Empty("还没有完成记录。\n勾掉的事项会按天记在这里。");
            return;
        }

        summary.Text = $"待办 {pending.Count} / 完成 {done.Count}";
        Section("待办", pending);
        foreach (var group in dated.GroupBy(t => t.CompletedAt!.Value.LocalDateTime.Date).OrderByDescending(g => g.Key))
            DaySection(group.Key, group.OrderByDescending(t => t.CompletedAt).ToList());
        Section("完成日期待确认", undated);
        if (pending.Count + done.Count == 0) Empty("还没有记录。\n想到什么，就在上面记下来。");
    }

    private static string DayLabel(DateTime day)
    {
        if (day.Date == DateTime.Today) return "今天";
        if (day.Date == DateTime.Today.AddDays(-1)) return "昨天";
        if (day.Date == DateTime.Today.AddDays(-2)) return "前天";
        return day.ToString("M月d日 ") + "周" + "日一二三四五六"[(int)day.DayOfWeek];
    }

    private void Empty(string text) => list.Children.Add(new TextBlock { Text = text, Foreground = Glass(200, 120, 136, 114), Margin = new Thickness(6, 30, 6, 0), TextWrapping = TextWrapping.Wrap, LineHeight = 24 });

    private void Section(string title, List<TaskItem> tasks)
    {
        if (tasks.Count == 0) return;
        list.Children.Add(new TextBlock { Text = title + "  " + tasks.Count, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromArgb(225, 88, 108, 82)), Margin = new Thickness(0, 8, 0, 8), FontSize = 12 });
        foreach (var t in tasks) AddCard(t);
    }

    private void DaySection(DateTime day, List<TaskItem> tasks)
    {
        var row = new DockPanel { Margin = new Thickness(0, 10, 0, 8) };
        var copy = Button("复制这天", () => ShowReport(DateOnly.FromDateTime(day)));
        copy.FontSize = 11; copy.Padding = new Thickness(8, 2, 8, 2); copy.Background = Brushes.Transparent; copy.BorderThickness = new Thickness(0); copy.Foreground = new SolidColorBrush(Color.FromArgb(215, 88, 108, 82));
        DockPanel.SetDock(copy, Dock.Right); row.Children.Add(copy);
        row.Children.Add(new TextBlock { Text = DayLabel(day) + "  " + tasks.Count, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromArgb(225, 88, 108, 82)), FontSize = 12 });
        list.Children.Add(row);
        foreach (var t in tasks) AddCard(t);
    }

    private void AddCard(TaskItem t)
    {
        var card = new Border
        {
            Background = t.IsCompleted ? Glass(125, 237, 242, 232) : Glass(170, 255, 255, 255),
            Padding = new Thickness(10), Margin = new Thickness(0, 0, 0, 7), CornerRadius = new CornerRadius(6)
        };
        var dock = new DockPanel(); card.Child = dock;
        if (recycle) { var restore = Button("恢复", () => { var c = Copy(t); c.DeletedAt = null; Save(c); }); DockPanel.SetDock(restore, Dock.Right); dock.Children.Add(restore); }
        else
        {
            var check = new CheckBox { IsChecked = t.IsCompleted, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 5, 9, 0) };
            check.Click += (_, _) => { var c = Copy(t); c.IsCompleted = check.IsChecked == true; c.CompletedAt = c.IsCompleted ? DateTimeOffset.Now : null; if (!Save(c)) check.IsChecked = t.IsCompleted; };
            DockPanel.SetDock(check, Dock.Left); dock.Children.Add(check);
        }
        var body = new StackPanel { Cursor = Cursors.Hand, Background = Brushes.Transparent };
        body.Children.Add(new TextBlock { Text = t.Title, TextWrapping = TextWrapping.Wrap, Foreground = t.IsCompleted ? new SolidColorBrush(Color.FromArgb(200, 116, 129, 106)) : Foreground, TextDecorations = t.IsCompleted ? TextDecorations.Strikethrough : null });
        if (!string.IsNullOrWhiteSpace(t.Notes)) body.Children.Add(new TextBlock { Text = t.Notes, FontSize = 12, Foreground = new SolidColorBrush(Color.FromArgb(210, 118, 134, 112)), TextWrapping = TextWrapping.Wrap, MaxHeight = 54, Margin = new Thickness(0, 5, 0, 0) });
        string label = t.IsCompleted
            ? t.CompletedAt?.LocalDateTime.ToString("MM-dd HH:mm 完成") ?? "完成日期未知 · 右键确认"
            : t.CreatedAt.LocalDateTime.Date < DateTime.Today ? CarryLabel(t.CreatedAt.LocalDateTime.Date) : "";
        if (label != "") body.Children.Add(new TextBlock { Text = label, FontSize = 10, Foreground = new SolidColorBrush(Color.FromArgb(205, 150, 162, 142)), Margin = new Thickness(0, 5, 0, 0) });
        body.MouseLeftButtonUp += (_, _) => { if (!recycle) Edit(t, false); };
        dock.Children.Add(body);
        var context = new ContextMenu();
        AddMenu(context, "编辑", () => Edit(t, false));
        if (t.IsCompleted && t.CompletedAt == null) AddMenu(context, "把完成日期设为今天", () => { var c = Copy(t); c.CompletedAt = DateTimeOffset.Now; Save(c); });
        AddMenu(context, recycle ? "恢复任务" : "移入回收区", () => { var c = Copy(t); c.DeletedAt = recycle ? null : DateTimeOffset.Now; Save(c); });
        card.ContextMenu = context; list.Children.Add(card);
    }

    private static string CarryLabel(DateTime created)
    {
        int days = (DateTime.Today - created.Date).Days;
        return days == 1 ? "昨天留下 · " + created.ToString("MM-dd") : $"{days} 天前留下 · " + created.ToString("MM-dd");
    }

    private void ResizeFromGrip()
    {
        if (Forms.Control.MouseButtons != Forms.MouseButtons.Left) return;
        var origin = Forms.Cursor.Position;
        double startWidth = ActualWidth, startHeight = ActualHeight;
        var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice;
        double sx = transform?.M11 ?? 1.0, sy = transform?.M22 ?? 1.0;
        while (Forms.Control.MouseButtons == Forms.MouseButtons.Left)
        {
            var cursor = Forms.Cursor.Position;
            Width = Math.Max(MinWidth, startWidth + (cursor.X - origin.X) / sx);
            Height = Math.Max(MinHeight, startHeight + (cursor.Y - origin.Y) / sy);
            Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
        }
        SaveSettings();
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
        AddMenu(menu, "透明度 " + (int)Math.Round(settings.PanelAlpha * 100) + "% →", CycleAlpha);
        AddMenu(menu, "字号 " + settings.TextSize + " → " + (settings.TextSize >= 18 ? 12 : settings.TextSize + 2), () => { settings.TextSize = settings.TextSize >= 18 ? 12 : settings.TextSize + 2; FontSize = settings.TextSize; SaveSettings(); });
        AddMenu(menu, settings.DesktopMode ? "切换为普通窗口" : "贴在桌面", () => { settings.DesktopMode = !settings.DesktopMode; if (settings.DesktopMode) desktop?.Attach(); else desktop?.Detach(); SaveSettings(); });
        AddMenu(menu, "隐藏到托盘", Hide);
        AddMenu(menu, "退出", Quit);
        menu.IsOpen = true;
    }

    private void CycleAlpha()
    {
        settings.PanelAlpha = settings.PanelAlpha switch
        {
            >= 1.0 => 0.94,
            >= 0.92 => 0.86,
            >= 0.84 => 0.76,
            >= 0.72 => 0.60,
            _ => 1.0
        };
        ApplyPanelLook();
        SaveSettings();
        status.Text = "透明度 " + (int)Math.Round(settings.PanelAlpha * 100) + "% · 已保存";
    }

    private void ShowReport(DateOnly day)
    {
        var window = new Window
        {
            Title = day.ToString("M月d日") + " 日报 · 可编辑后复制", Width = 560, Height = 600,
            WindowStartupLocation = WindowStartupLocation.CenterScreen, FontFamily = FontFamily, FontSize = 14,
            Background = Brush("#FAFBF6")
        };
        var panel = new DockPanel { Margin = new Thickness(18) }; window.Content = panel;
        var text = new TextBox { Text = DailyReport.Build(items, day), AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(12) };
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
            new TaskItem { Title = "确认接口超时的重试策略", CreatedAt = DateTimeOffset.Now.AddDays(-2) },
            new TaskItem { Title = "修复文档导出乱码", CreatedAt = DateTimeOffset.Now, IsCompleted = true, CompletedAt = DateTimeOffset.Now, Notes = "已验证中文导出正常。" },
            new TaskItem { Title = "调整首页布局", CreatedAt = DateTimeOffset.Now.AddDays(-1), IsCompleted = true, CompletedAt = DateTimeOffset.Now.AddDays(-1) } }) store.Save(t);
    }
    private void CapturePreview()
    {
        var image = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32); image.Render(this);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image));
        string path = Path.Combine(Environment.CurrentDirectory, "work", "preview-" + mode + ".png"); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var file = File.Create(path); png.Save(file);
    }
}
