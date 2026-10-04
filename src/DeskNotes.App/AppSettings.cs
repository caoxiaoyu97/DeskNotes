using System;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace DeskNotes.App;

public sealed class AppSettings
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private static bool portableProbed, portableWritable;

    /// <summary>Folder the program was started from.</summary>
    public static string ExeDirectory { get; } = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static string LegacyRoot { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeskNotes");

    /// <summary>
    /// Settings and records live next to the program, so it is always obvious where
    /// the data is and the whole folder can be moved or copied. If that folder is not
    /// writable (read-only media, a locked-down install directory), fall back to the
    /// per-user location.
    /// </summary>
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
    public static string ConfigDirectory => Path.Combine(BaseDirectory, "config");
    public static string ConfigPath => Path.Combine(ConfigDirectory, "settings.json");
    public static string DefaultDataDirectory => Path.Combine(BaseDirectory, "data");
    private static string LegacyConfigPath => Path.Combine(LegacyRoot, "settings.json");

    /// <summary>Set when the first run in a new location brought old data across.</summary>
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

    /// <summary>Loads settings, migrating from the older per-user location on first run.</summary>
    public static AppSettings LoadOrMigrate()
    {
        StartupNote = "";
        var settings = Read(ConfigPath) ?? new AppSettings();
        if (File.Exists(ConfigPath)) return settings;

        var legacy = Read(LegacyConfigPath);
        if (legacy is not null)
        {
            // Keep how the note looked, then bring the records over so nothing looks lost.
            settings.Left = legacy.Left; settings.Top = legacy.Top;
            settings.Width = legacy.Width; settings.Height = legacy.Height;
            settings.TextSize = legacy.TextSize; settings.PanelAlpha = legacy.PanelAlpha;
            settings.DesktopMode = legacy.DesktopMode;
            int copied = CopyRecords(legacy.DataDirectory, settings.DataDirectory);
            StartupNote = copied > 0
                ? $"已把 {copied} 条旧记录搬到 {settings.DataDirectory}"
                : $"数据位置已改为 {settings.DataDirectory}";
        }
        else
        {
            StartupNote = "数据位置：" + settings.DataDirectory;
        }
        try { settings.Save(); } catch (IOException) { }
        return settings;
    }

    public void Save()
    {
        Directory.CreateDirectory(ConfigDirectory);
        string path = ConfigPath;
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(this, Options));
        File.Move(path + ".tmp", path, true);
    }

    private static AppSettings? Read(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) : null; }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Copies monthly records (and backups) when the destination has no records yet.</summary>
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
}
