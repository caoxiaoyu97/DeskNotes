using System;
using System.IO;
using System.Text.Json;

namespace DeskNotes.App;

public sealed class AppSettings
{
    public static string BaseDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeskNotes");
    public string DataDirectory { get; set; } = Path.Combine(BaseDirectory, "data");
    public double Left { get; set; } = 60;
    public double Top { get; set; } = 80;
    public double Width { get; set; } = 410;
    public double Height { get; set; } = 650;
    public bool DesktopMode { get; set; } = true;
    public double TextSize { get; set; } = 14;
    /// <summary>1.0 = opaque panel, lower values let the wallpaper show through.</summary>
    public double PanelAlpha { get; set; } = 0.80;
    public static AppSettings Load()
    {
        string path = Path.Combine(BaseDirectory, "settings.json");
        try { return File.Exists(path) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? new() : new(); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }
    public void Save()
    {
        Directory.CreateDirectory(BaseDirectory);
        string path = Path.Combine(BaseDirectory, "settings.json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(path + ".tmp", path, true);
    }
}
