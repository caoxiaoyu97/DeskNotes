using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DeskNotes.Core;

namespace DeskNotes.App;

/// <summary>Remote address, credentials and the manual sync actions.</summary>
public sealed class SyncWindow : Window
{
    private readonly AppSettings settings;
    private readonly GitSync git;
    private readonly TextBox remote = new() { Padding = new Thickness(6) };
    private readonly TextBox branch = new() { Padding = new Thickness(6), Text = "main" };
    private readonly TextBox user = new() { Padding = new Thickness(6) };
    private readonly PasswordBox token = new() { Padding = new Thickness(6) };
    private readonly CheckBox autoPush = new() { Content = "修改后自动提交并推送", IsChecked = true };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0), Foreground = new SolidColorBrush(Color.FromRgb(70, 90, 70)) };
    private readonly Button[] buttons = new Button[4];

    public SyncWindow(AppSettings settings, Action onChanged)
    {
        this.settings = settings;
        git = new GitSync(AppSettings.WorkspaceDirectory);
        Title = "Git 同步 · 桌面便签";
        Icon = AppIcon.Window;
        Width = 560; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = new FontFamily("Microsoft YaHei UI"); FontSize = 13;
        Background = new SolidColorBrush(Color.FromRgb(250, 251, 246));

        var panel = new StackPanel { Margin = new Thickness(22) };
        Content = panel;
        panel.Children.Add(new TextBlock { Text = "把记录同步到 Git 仓库", FontSize = 19, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock
        {
            Text = "配置和记录都在同一个文件夹里：" + AppSettings.WorkspaceDirectory + "\n" +
                   "这个文件夹本身就是 Git 仓库，带走它等于带走全部数据。" + GitSync.GitHint,
            TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(110, 120, 106)),
            Margin = new Thickness(0, 6, 0, 16)
        });

        panel.Children.Add(Labeled("仓库地址（https://github.com/用户名/仓库名.git）", remote));
        panel.Children.Add(Labeled("分支", branch));
        panel.Children.Add(Labeled("用户名", user));
        panel.Children.Add(Labeled("访问令牌（只保存在本地 .credentials.json，不会提交）", token));
        panel.Children.Add(autoPush);

        remote.Text = settings.SyncRemoteUrl;
        branch.Text = settings.SyncBranch;
        user.Text = settings.SyncUserName;
        token.Password = AppSettings.SyncToken;
        autoPush.IsChecked = settings.SyncAutoPush;

        var row = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
        buttons[0] = Add(row, "保存并检查仓库", () => SaveAndCheck(onChanged));
        buttons[1] = Add(row, "立即上传", () => Upload());
        buttons[2] = Add(row, "从远端下载（覆盖本地）", () => Download());
        buttons[3] = Add(row, "打开文件夹", OpenFolder);
        panel.Children.Add(row);
        panel.Children.Add(status);

        if (!GitSync.Available) SetBusy(true);
        else status.Text = "远端仓库不存在时会自动创建一个私有仓库（GitHub / Gitee / GitLab）。";
    }

    private static TextBlock Label(string text) => new()
    {
        Text = text, FontSize = 12, Margin = new Thickness(0, 10, 0, 4),
        Foreground = new SolidColorBrush(Color.FromRgb(96, 108, 94))
    };

    private static StackPanel Labeled(string label, Control input)
    {
        var host = new StackPanel();
        host.Children.Add(Label(label));
        host.Children.Add(input);
        return host;
    }

    private Button Add(Panel host, string text, Action action)
    {
        var button = new Button { Content = text, Margin = new Thickness(0, 0, 8, 8), Padding = new Thickness(12, 6, 12, 6), BorderThickness = new Thickness(0) };
        button.Background = new SolidColorBrush(Color.FromRgb(226, 236, 219));
        button.Click += (_, _) => action();
        host.Children.Add(button);
        return button;
    }

    private void SetBusy(bool busy)
    {
        foreach (var button in buttons) if (button is not null) button.IsEnabled = !busy;
        Cursor = busy ? System.Windows.Input.Cursors.Wait : null;
    }

    private void Report(SyncOutcome outcome)
    {
        status.Text = outcome.Message;
        status.Foreground = outcome.Ok
            ? new SolidColorBrush(Color.FromRgb(70, 110, 70))
            : new SolidColorBrush(Color.FromRgb(160, 50, 50));
    }

    private void ApplyToSettings()
    {
        settings.SyncRemoteUrl = remote.Text.Trim();
        settings.SyncBranch = branch.Text.Trim().Length > 0 ? branch.Text.Trim() : "main";
        settings.SyncUserName = user.Text.Trim();
        settings.SyncAutoPush = autoPush.IsChecked == true;
        settings.SyncEnabled = settings.SyncRemoteUrl.Length > 0;
        settings.Save();
        AppSettings.SaveCredentials(token.Password, settings.SyncUserName);
    }

    private async void SaveAndCheck(Action onChanged)
    {
        if (!GitSync.Available) { Report(SyncOutcome.Fail(GitSync.GitHint)); return; }
        if (GitSync.ParseRemote(remote.Text) is null)
        {
            Report(SyncOutcome.Fail("地址看不懂。示例：https://github.com/你的用户名/desknotes.git"));
            return;
        }
        SetBusy(true);
        Report(SyncOutcome.Done("正在检查远端仓库…"));
        string url = remote.Text.Trim(), owner = user.Text.Trim(), secret = token.Password, name = branch.Text.Trim();
        var outcome = await Task.Run(async () =>
        {
            var remoteCheck = await GitSync.EnsureRemoteRepositoryAsync(url, owner, secret);
            if (!remoteCheck.Ok) return remoteCheck;
            var prepared = git.EnsureRepository(name);
            if (!prepared.Ok) return prepared;
            var set = git.SetRemote(GitSync.ParseRemote(url)!.Url, name);
            return set.Ok ? SyncOutcome.Done(remoteCheck.Message + "；" + set.Message) : set;
        });
        Report(outcome);
        SetBusy(false);
        if (outcome.Ok) { ApplyToSettings(); onChanged(); }
    }

    private async void Upload()
    {
        SetBusy(true);
        string owner = user.Text.Trim(), secret = token.Password, name = branch.Text.Trim();
        var outcome = await Task.Run(() =>
        {
            var prepared = git.EnsureRepository(name);
            if (!prepared.Ok) return prepared;
            var commit = git.Commit("更新记录 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
            if (!commit.Ok) return commit;
            var push = git.Push(name, owner, secret);
            return push.Ok ? SyncOutcome.Done(commit.Message + "；" + push.Message) : push;
        });
        Report(outcome);
        SetBusy(false);
        if (outcome.Ok) ApplyToSettings();
    }

    private async void Download()
    {
        string warning = "会用远端内容覆盖本地记录。\n本地现有记录会先备份到 data\\backups 下。\n\n确定继续吗？";
        if (MessageBox.Show(this, warning, "从远端下载", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        SetBusy(true);
        string owner = user.Text.Trim(), secret = token.Password, name = branch.Text.Trim();
        var outcome = await Task.Run(() =>
        {
            var prepared = git.EnsureRepository(name);
            if (!prepared.Ok) return prepared;
            return git.Download(name, owner, secret);
        });
        Report(outcome);
        SetBusy(false);
        if (outcome.Ok) { ApplyToSettings(); MessageBox.Show(this, "下载完成。关闭本窗口后便签会重新读取记录。", "桌面便签"); }
    }

    private void OpenFolder()
    {
        System.IO.Directory.CreateDirectory(AppSettings.WorkspaceDirectory);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AppSettings.WorkspaceDirectory) { UseShellExecute = true });
    }
}
