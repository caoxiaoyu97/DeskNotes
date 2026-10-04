using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeskNotes.App;

/// <summary>
/// Settings plus the layout of the user workspace.
///
/// Everything the user owns - settings and records - lives in one folder next to the
/// program (<c>DeskNotes</c>). That folder can be copied, archived, or made into a Git
/// repository, and it carries the whole user state with it.
/// </summary>
public sealed class AppSettings
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private static bool portableProbed, portableWritable;

    public static string ExeDirectory { get; } = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static string LegacyRoot { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeskNotes");

    public static bool IsPortable
    {
        get
        {
            if (!portableProbed)
            {
                portableProbed = true;
                portableWritable = CanWrite(ExeDirectory);
            }
            return portableWritable;
        }
    }

    public static string BaseDirectory => IsPortable ? ExeDirectory : LegacyRoot;

    /// <summary>The single carry-able folder: config, records, backups, Git metadata.</summary>
    public static string WorkspaceDirectory { get; } = Path.Combine(BaseDirectory, "DeskNotes");
    public static string ConfigDirectory { get; } = Path.Combine(WorkspaceDirectory, "config");
    public static string ConfigPath { get; } = Path.Combine(ConfigDirectory, "settings.json");
    public static string DefaultDataDirectory { get; } = Path.Combine(WorkspaceDirectory, "data");
    public static string CredentialPath { get; } = Path.Combine(WorkspaceDirectory, ".credentials.json");
    private static string LegacyConfigPath { get; } = Path.Combine(LegacyRoot, "settings.json");

    public static string StartupNote { get; private set; } = "";

    public string DataDirectory { get; set; } = DefaultDataDirectory;
    public double Left { get; set; } = 60;
    public double Top { get; set; } = 80;
    public double Width { get; set; } = 410;
    public double Height { get; set; } = 650;
    public bool DesktopMode { get; set; } = true;
    public double TextSize { get; set; } = 14;
    /// <summary>1.0 = opaque panel, lower values let the wallpaper show through.</summary>
    public double PanelAlpha { get; set; } = 0.80;

    public bool SyncEnabled { get; set; }
    public string SyncRemoteUrl { get; set; } = "";
    public string SyncBranch { get; set; } = "main";
    public string SyncUserName { get; set; } = "";
    public bool SyncAutoPush { get; set; } = true;

    /// <summary>Stored outside the committed files, never written into the repository.</summary>
    public static string SyncToken { get; set; } = "";

    public static AppSettings LoadOrMigrate()
    {
        StartupNote = "";
        PrepareWorkspace();
        var settings = Read(ConfigPath) ?? new AppSettings();
        if (File.Exists(ConfigPath)) { LoadCredentials(); return settings; }

        var legacy = Read(LegacyConfigPath);
        if (legacy is not null)
        {
            // Adopt how the note looked, then bring the records across.
            settings.Left = legacy.Left; settings.Top = legacy.Top;
            settings.Width = legacy.Width; settings.Height = legacy.Height;
            settings.TextSize = legacy.TextSize; settings.PanelAlpha = legacy.PanelAlpha;
            settings.DesktopMode = legacy.DesktopMode;
            // The pre-0.5 layout kept records in <base>\data, which PrepareWorkspace has
            // just moved into the workspace. Carrying that stale path over would point the
            // app at an empty folder, so only a deliberately customised path is adopted.
            if (legacy.DataDirectory.Length > 0 && !SamePath(legacy.DataDirectory, Path.Combine(BaseDirectory, "data")))
                settings.DataDirectory = legacy.DataDirectory;
            int copied = CopyRecords(legacy.DataDirectory, settings.DataDirectory);
            StartupNote = copied > 0 ? $"已把 {copied} 条旧记录搬到 {settings.DataDirectory}" : "";
        }
        LoadCredentials();
        try { settings.Save(); } catch (IOException) { }
        return settings;
    }

    /// <summary>
    /// Moves the pre-0.5 layout (<c>config</c> and <c>data</c> next to the exe) into the
    /// single workspace folder. Each folder is moved only when the new one is absent, so
    /// a half-finished move is simply redone on the next start.
    /// </summary>
    private static void PrepareWorkspace()
    {
        if (!IsPortable) return;
        try
        {
            Directory.CreateDirectory(WorkspaceDirectory);
            Move(Path.Combine(BaseDirectory, "config"), ConfigDirectory);
            Move(Path.Combine(BaseDirectory, "data"), DefaultDataDirectory);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static void Move(string from, string to)
    {
        if (!Directory.Exists(from) || Directory.Exists(to)) return;
        try { Directory.Move(from, to); }
        catch (IOException)
        {
            // Cross-volume or locked: fall back to copying, then leave the original in place.
            Directory.CreateDirectory(to);
            foreach (string file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            {
                string target = Path.Combine(to, Path.GetRelativePath(from, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, true);
            }
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(ConfigDirectory);
        string path = ConfigPath;
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(this, Options));
        File.Move(path + ".tmp", path, true);
    }

    private sealed record Credentials(string Token, string User);

    private static void LoadCredentials()
    {
        try
        {
            if (!File.Exists(CredentialPath)) return;
            var parsed = JsonSerializer.Deserialize<Credentials>(File.ReadAllText(CredentialPath));
            if (parsed is not null) SyncToken = parsed.Token;
        }
        catch (Exception e) when (e is IOException or JsonException) { }
    }

    public static void SaveCredentials(string token, string user)
    {
        SyncToken = token;
        Directory.CreateDirectory(WorkspaceDirectory);
        File.WriteAllText(CredentialPath, JsonSerializer.Serialize(new Credentials(token, user), Options));
    }

    private static AppSettings? Read(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) : null; }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    private static int CopyRecords(string from, string to)
    {
        try
        {
            string source = Path.Combine(from, "records");
            string target = Path.Combine(to, "records");
            if (!Directory.Exists(source)) return 0;
            if (Directory.Exists(target) && Directory.EnumerateFiles(target, "*.md").Any()) return 0;
            Directory.CreateDirectory(target);
            int copied = 0;
            foreach (string file in Directory.EnumerateFiles(source, "*.md"))
            {
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)), false);
                copied++;
            }
            string sourceBackups = Path.Combine(from, "backups");
            if (Directory.Exists(sourceBackups))
            {
                string targetBackups = Path.Combine(to, "backups");
                Directory.CreateDirectory(targetBackups);
                foreach (string file in Directory.EnumerateFiles(sourceBackups))
                    try { File.Copy(file, Path.Combine(targetBackups, Path.GetFileName(file)), false); }
                    catch (IOException) { }
            }
            return copied;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return 0; }
    }

    private static bool CanWrite(string dir)
    {
        try
        {
            string probe = Path.Combine(dir, ".desknotes-write-test");
            File.WriteAllText(probe, "x");
            File.Delete(probe);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    private static bool SamePath(string left, string right)
    {
        try
        {
            static string Normalize(string value) => Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }
}
