using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;

namespace DeskNotes.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        bool preview = Array.IndexOf(args, "--preview") >= 0;
        int startMode = 0;
        int modeAt = Array.IndexOf(args, "--mode");
        if (modeAt >= 0 && modeAt + 1 < args.Length && int.TryParse(args[modeAt + 1], out int parsed)) startMode = Math.Clamp(parsed, 0, 2);
        using var mutex = new Mutex(true, preview ? "Local\\DeskNotes.Preview" : "Local\\DeskNotes.App", out bool first);
        if (!first) { MessageBox.Show("桌面便签已经运行，可以从系统托盘打开。", "桌面便签"); return; }
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.DispatcherUnhandledException += (_, e) =>
        {
            Directory.CreateDirectory(AppSettings.BaseDirectory);
            File.AppendAllText(Path.Combine(AppSettings.BaseDirectory, "errors.log"), DateTimeOffset.Now + " " + e.Exception + Environment.NewLine);
            MessageBox.Show("操作未完成，记录不会被主动清空。\n" + e.Exception.Message, "桌面便签");
            e.Handled = true;
        };
        var button = new Style(typeof(System.Windows.Controls.Button));
        button.Setters.Add(new Setter(System.Windows.Controls.Control.PaddingProperty, new Thickness(10, 6, 10, 6)));
        button.Setters.Add(new Setter(System.Windows.Controls.Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(239, 242, 235))));
        button.Setters.Add(new Setter(System.Windows.Controls.Control.BorderThicknessProperty, new Thickness(0)));
        button.Setters.Add(new Setter(System.Windows.Controls.Control.CursorProperty, System.Windows.Input.Cursors.Hand));
        app.Resources.Add(typeof(System.Windows.Controls.Button), button);
        app.Run(new MainWindow(preview, Array.IndexOf(args, "--demo") >= 0, startMode));
    }
}
