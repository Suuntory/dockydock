using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DockyDock;

/// <summary>Logo de l'appli (celui de l'exe, défini par ApplicationIcon dans le .csproj), pour les fenêtres WPF.</summary>
internal static class AppIcon
{
    public static ImageSource? Source { get; } = Load();

    private static ImageSource? Load()
    {
        try
        {
            if (Environment.ProcessPath is not { } exe) return null;
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(exe);
            if (icon == null) return null;
            var source = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch { return null; }
    }
}
