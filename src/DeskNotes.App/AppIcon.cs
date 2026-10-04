using System;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace DeskNotes.App;

/// <summary>The app icon, embedded as a WPF resource so windows, dialogs and the tray all share it.</summary>
internal static class AppIcon
{
    private const string Resource = "pack://application:,,,/assets/DeskNotes.ico";

    public static BitmapImage? Window { get; } = LoadWindowIcon();

    private static BitmapImage? LoadWindowIcon()
    {
        try { return new BitmapImage(new Uri(Resource)); }
        catch (Exception e) when (e is UriFormatException or IOException or NotSupportedException) { return null; }
    }

    /// <summary>
    /// Smallest frame for the notification area, which renders at 16 px. A tray icon
    /// must never be able to break startup, so every failure falls through to the next
    /// source and finally to a system icon.
    /// </summary>
    public static System.Drawing.Icon Tray()
    {
        try
        {
            using Stream? stream = Application.GetResourceStream(new Uri(Resource))?.Stream;
            if (stream is not null) return new System.Drawing.Icon(stream, new System.Drawing.Size(16, 16));
        }
        catch (Exception e) when (e is IOException or ArgumentException or InvalidOperationException or NotSupportedException) { }
        try
        {
            if (Environment.ProcessPath is { } path && System.Drawing.Icon.ExtractAssociatedIcon(path) is { } icon) return icon;
        }
        catch (Exception e) when (e is IOException or ArgumentException) { }
        return System.Drawing.SystemIcons.Application;
    }
}
