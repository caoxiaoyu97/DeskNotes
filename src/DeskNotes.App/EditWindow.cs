using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DeskNotes.Core;

namespace DeskNotes.App;

public sealed class EditWindow : Window
{
    public EditWindow(TaskItem item, bool quick, Func<TaskItem, bool> save)
    {
        Title = quick ? "随手记一下" : "编辑事项";
        Width = 460; Height = quick ? 260 : 410;
        MinWidth = 360; MinHeight = 240;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(250, 250, 245));
        FontFamily = new FontFamily("Microsoft YaHei UI"); FontSize = 14;
        ShowInTaskbar = true; Topmost = quick;
        var panel = new StackPanel { Margin = new Thickness(22) };
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        panel.Children.Add(new TextBlock { Text = quick ? "想到什么，先记下来" : "任务与成果", FontSize = 20, Margin = new Thickness(0, 0, 0, 14) });
        var title = new TextBox { Text = item.Title, Padding = new Thickness(8), MaxLength = 1000 };
        panel.Children.Add(title);
        var notes = new TextBox { Text = item.Notes, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 110, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(8), Margin = new Thickness(0, 10, 0, 0) };
        if (!quick)
        {
            panel.Children.Add(new TextBlock { Text = "备注 / 实际成果", Margin = new Thickness(0, 14, 0, 0) });
            panel.Children.Add(notes);
        }
        var completed = new CheckBox { Content = "这件事已经完成", IsChecked = item.IsCompleted, Margin = new Thickness(0, 12, 0, 8) };
        panel.Children.Add(completed);
        var error = new TextBlock { Foreground = Brushes.Firebrick, TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(error);
        var button = new Button { Content = quick ? "保存  ↵" : "保存修改", HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        panel.Children.Add(button);
        void Submit()
        {
            if (string.IsNullOrWhiteSpace(title.Text)) { error.Text = "写一句任务内容再保存。"; return; }
            var updated = new TaskItem { Id = item.Id, Title = title.Text.Trim(), Notes = notes.Text, CreatedAt = item.CreatedAt, DeletedAt = item.DeletedAt,
                IsCompleted = completed.IsChecked == true,
                CompletedAt = completed.IsChecked == true ? (item.IsCompleted ? item.CompletedAt : DateTimeOffset.Now) : null };
            if (save(updated)) Close();
            else error.Text = "未保存，输入仍保留。若发生外部修改，请复制内容后关闭并重新打开。";
        }
        button.Click += (_, _) => Submit();
        title.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Submit(); e.Handled = true; } };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        Loaded += (_, _) => { title.Focus(); title.CaretIndex = title.Text.Length; };
    }
}
