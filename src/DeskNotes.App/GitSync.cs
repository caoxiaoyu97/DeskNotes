using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace DeskNotes.App;

public sealed record SyncOutcome(bool Ok, string Message)
{
    public static SyncOutcome Fail(string message) => new(false, message);
    public static SyncOutcome Done(string message) => new(true, message);
}

/// <summary>
/// Turns the user workspace into a Git repository and syncs it to a remote.
///
/// The local <c>git</c> command does the work (so any host works and the user can inspect
/// or fix the repository by hand). Credentials are passed per command through a temporary
/// http header, never written into the repository or its configuration.
/// </summary>
public sealed class GitSync
{
    private readonly string root;

    public GitSync(string workspaceRoot) => root = workspaceRoot;

    /// <summary>Located once; a portable <c>git</c> folder next to the exe also works.</summary>
    public static string? GitPath { get; } = FindGit();
    public static bool Available => GitPath is not null;
    public static string GitHint => Available
        ? ""
        : "没有找到 git。安装 Git 后重启本程序即可启用同步，或把便携版 git 放到程序目录下的 git\\cmd\\git.exe。";

    private static string? FindGit()
    {
        var candidates = new List<string>
        {
            Path.Combine(AppSettings.ExeDirectory, "git", "cmd", "git.exe"),
            Path.Combine(AppSettings.ExeDirectory, "git", "bin", "git.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "cmd", "git.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Git", "cmd", "git.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Git", "cmd", "git.exe")
        };
        foreach (string path in candidates)
            if (File.Exists(path)) return path;
        return "git"; // rely on PATH
    }

    private (int Code, string Output) Run(string arguments)
    {
        var info = new ProcessStartInfo(GitPath ?? "git", arguments)
        {
            WorkingDirectory = root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        info.Environment["GIT_TERMINAL_PROMPT"] = "0";       // never hang on a prompt
        info.Environment["GCM_INTERACTIVE"] = "never";
        try
        {
            using var process = Process.Start(info);
            if (process is null) return (-1, "无法启动 git");
            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            process.WaitForExit(120000);
            string output = (stdout + "\n" + stderr).Trim();
            return (process.HasExited ? process.ExitCode : -1, output);
        }
        catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception)
        {
            return (-1, e.Message);
        }
    }

    private static string CredentialArgs(string user, string token)
    {
        if (token.Length == 0) return "";
        string basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{token}"));
        return $"-c http.extraHeader=\"Authorization: Basic {basic}\" ";
    }

    /// <summary>Creates the repository and the housekeeping files if they are missing.</summary>
    public SyncOutcome EnsureRepository(string branch)
    {
        if (!Available) return SyncOutcome.Fail(GitHint);
        Directory.CreateDirectory(root);
        if (!Directory.Exists(Path.Combine(root, ".git")))
        {
            var init = Run($"init -b {branch}");
            if (init.Code != 0) init = Run("init");       // older git has no -b
            if (init.Code != 0) return SyncOutcome.Fail("git init 失败：" + init.Output);
        }
        WriteIfMissing(".gitignore", IgnoreFile);
        WriteIfMissing(".gitattributes", "* -text\n");
        Run("config core.autocrlf false");
        Run("config core.safecrlf false");
        // A local identity keeps commits working even when the machine has none configured.
        Run("config user.name \"DeskNotes\"");
        Run("config user.email \"desknotes@localhost\"");
        return SyncOutcome.Done("仓库已就绪");
    }

    private const string IgnoreFile =
        "# 同步时不需要带上这些\n" +
        "data/backups/\n" +
        "*.lock\n" +
        "*.tmp\n" +
        "*.bak\n" +
        ".credentials.json\n" +
        "*.log\n";

    private void WriteIfMissing(string name, string content)
    {
        string path = Path.Combine(root, name);
        if (!File.Exists(path)) File.WriteAllText(path, content, new UTF8Encoding(false));
    }

    public SyncOutcome Commit(string message)
    {
        if (!Available) return SyncOutcome.Fail(GitHint);
        var add = Run("add -A");
        if (add.Code != 0) return SyncOutcome.Fail("git add 失败：" + add.Output);
        var status = Run("status --porcelain");
        if (status.Output.Length == 0) return SyncOutcome.Done("没有变化需要提交");
        var commit = Run($"commit -m \"{message}\"");
        if (commit.Code != 0) return SyncOutcome.Fail("git commit 失败：" + commit.Output);
        return SyncOutcome.Done("已提交：" + message);
    }

    public SyncOutcome Push(string branch, string user, string token)
    {
        if (!Available) return SyncOutcome.Fail(GitHint);
        var push = Run($"{CredentialArgs(user, token)}push -u origin {branch}");
        if (push.Code != 0) return SyncOutcome.Fail("推送失败：" + Summarize(push.Output));
        return SyncOutcome.Done("已推送到远端");
    }

    /// <summary>Fetches and replaces the working tree, keeping a copy of the local records first.</summary>
    public SyncOutcome Download(string branch, string user, string token)
    {
        if (!Available) return SyncOutcome.Fail(GitHint);
        var remote = Run("remote get-url origin");
        if (remote.Code != 0) return SyncOutcome.Fail("还没有配置远端地址");
        string backup = BackupRecords();
        var fetch = Run($"{CredentialArgs(user, token)}fetch origin {branch}");
        if (fetch.Code != 0) return SyncOutcome.Fail("拉取失败：" + Summarize(fetch.Output));
        var reset = Run($"reset --hard origin/{branch}");
        if (reset.Code != 0) return SyncOutcome.Fail("更新工作区失败：" + Summarize(reset.Output));
        return SyncOutcome.Done(backup.Length > 0 ? "已下载远端内容（本地旧记录备份在 " + backup + "）" : "已下载远端内容");
    }

    private string BackupRecords()
    {
        try
        {
            string records = Path.Combine(root, "data", "records");
            if (!Directory.Exists(records)) return "";
            string target = Path.Combine(root, "data", "backups", "before-download-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(target);
            foreach (string file in Directory.EnumerateFiles(records, "*.md"))
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)), true);
            return Path.GetRelativePath(root, target);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return ""; }
    }

    public SyncOutcome SetRemote(string url, string branch)
    {
        if (!Available) return SyncOutcome.Fail(GitHint);
        Run("remote remove origin");
        var add = Run($"remote add origin \"{url}\"");
        if (add.Code != 0) return SyncOutcome.Fail("设置远端失败：" + Summarize(add.Output));
        Run($"branch -M {branch}");
        return SyncOutcome.Done("远端已设置");
    }

    public SyncOutcome HasRemote()
    {
        if (!Available) return SyncOutcome.Fail(GitHint);
        var remote = Run("remote get-url origin");
        return remote.Code == 0 && remote.Output.Length > 0
            ? SyncOutcome.Done(remote.Output)
            : SyncOutcome.Fail("还没有配置远端地址");
    }

    private static string Summarize(string output)
    {
        string text = output.Replace("\r", "").Trim();
        return text.Length > 400 ? text[..400] + "…" : text;
    }

    // ---- remote host helpers: check whether the repository exists, create it if not ----

    public sealed record RemoteTarget(string Host, string Owner, string Repo, bool IsHttps, string Url)
    {
        public bool IsKnownHost => Host is "github.com" or "gitee.com" or "gitlab.com";
    }

    public static RemoteTarget? ParseRemote(string url)
    {
        string text = (url ?? "").Trim();
        if (text.Length == 0) return null;
        if (text.StartsWith("git@", StringComparison.Ordinal))
        {
            int colon = text.IndexOf(':');
            if (colon < 0) return null;
            text = "https://" + text[4..colon] + "/" + text[(colon + 1)..];
        }
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return null;
        string path = uri.AbsolutePath.Trim('/');
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) path = path[..^4];
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return null;
        string repo = parts[^1];
        string owner = string.Join('/', parts[..^1]);
        return new RemoteTarget(uri.Host.ToLowerInvariant(), owner, repo, uri.Scheme == "https", $"https://{uri.Host}/{owner}/{repo}.git");
    }

    /// <summary>Checks the remote repository through the host API and creates it when missing.</summary>
    public static async Task<SyncOutcome> EnsureRemoteRepositoryAsync(string url, string user, string token)
    {
        var target = ParseRemote(url);
        if (target is null) return SyncOutcome.Fail("看不懂这个地址，示例：https://github.com/你的用户名/desknotes.git");
        if (!target.IsKnownHost) return SyncOutcome.Done("非 GitHub/Gitee/GitLab，跳过自动建仓，直接按已有地址同步");
        if (token.Length == 0) return SyncOutcome.Fail("需要填写访问令牌才能检查或创建仓库");

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("DeskNotes");
            if (target.Host == "github.com")
            {
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
                var check = await http.GetAsync($"https://api.github.com/repos/{target.Owner}/{target.Repo}");
                if (check.IsSuccessStatusCode) return SyncOutcome.Done("远端仓库已存在");
                if ((int)check.StatusCode != 404) return SyncOutcome.Fail("检查仓库失败：" + (int)check.StatusCode);
                string body = JsonSerializer.Serialize(new { name = target.Repo, @private = true, auto_init = false });
                var create = await http.PostAsync("https://api.github.com/user/repos", new StringContent(body, Encoding.UTF8, "application/json"));
                return create.IsSuccessStatusCode
                    ? SyncOutcome.Done($"已在 GitHub 创建私有仓库 {target.Repo}")
                    : SyncOutcome.Fail("创建仓库失败：" + (int)create.StatusCode + " " + await create.Content.ReadAsStringAsync());
            }
            if (target.Host == "gitee.com")
            {
                var check = await http.GetAsync($"https://gitee.com/api/v5/repos/{target.Owner}/{target.Repo}?access_token={Uri.EscapeDataString(token)}");
                if (check.IsSuccessStatusCode) return SyncOutcome.Done("远端仓库已存在");
                if ((int)check.StatusCode != 404) return SyncOutcome.Fail("检查仓库失败：" + (int)check.StatusCode);
                var form = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["access_token"] = token,
                    ["name"] = target.Repo,
                    ["private"] = "true",
                    ["auto_init"] = "false"
                });
                var create = await http.PostAsync("https://gitee.com/api/v5/user/repos", form);
                return create.IsSuccessStatusCode
                    ? SyncOutcome.Done($"已在 Gitee 创建私有仓库 {target.Repo}")
                    : SyncOutcome.Fail("创建仓库失败：" + (int)create.StatusCode + " " + await create.Content.ReadAsStringAsync());
            }
            string projectId = Uri.EscapeDataString(target.Owner + "/" + target.Repo);
            http.DefaultRequestHeaders.Add("PRIVATE-TOKEN", token);
            var gitlab = await http.GetAsync($"https://gitlab.com/api/v4/projects/{projectId}");
            if (gitlab.IsSuccessStatusCode) return SyncOutcome.Done("远端仓库已存在");
            if ((int)gitlab.StatusCode != 404) return SyncOutcome.Fail("检查仓库失败：" + (int)gitlab.StatusCode);
            string payload = JsonSerializer.Serialize(new { name = target.Repo, path = target.Repo, visibility = "private" });
            var made = await http.PostAsync("https://gitlab.com/api/v4/projects", new StringContent(payload, Encoding.UTF8, "application/json"));
            return made.IsSuccessStatusCode
                ? SyncOutcome.Done($"已在 GitLab 创建私有仓库 {target.Repo}")
                : SyncOutcome.Fail("创建仓库失败：" + (int)made.StatusCode + " " + await made.Content.ReadAsStringAsync());
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return SyncOutcome.Fail("网络请求失败：" + e.Message);
        }
    }
}
