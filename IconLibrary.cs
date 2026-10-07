using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DockyDock;

/// <summary>Icônes personnalisées : le dossier "icons" du projet (ou, à défaut, celui à côté de l'exe).</summary>
internal static class IconLibrary
{
    private static readonly string[] Extensions = { ".png", ".ico", ".jpg", ".jpeg", ".bmp" };
    private static readonly Dictionary<string, (DateTime Stamp, ImageSource? Image)> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static string? _dir;

    public static string Dir => _dir ??= FindDir();

    private static string FindDir()
    {
        // En développement l'exe est dans bin\Debug\... : on remonte jusqu'au dossier du projet.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
            if (File.Exists(System.IO.Path.Combine(dir.FullName, "DockyDock.csproj")))
                return Ensure(System.IO.Path.Combine(dir.FullName, "icons"));
        return Ensure(System.IO.Path.Combine(AppContext.BaseDirectory, "icons"));
    }

    private static string Ensure(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }

    public static List<string> List() =>
        Directory.GetFiles(Dir)
            .Where(f => Extensions.Contains(System.IO.Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .OrderBy(f => System.IO.Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Icône personnalisée d'un élément, ou null si aucune (ou fichier introuvable).</summary>
    public static ImageSource? LoadFor(DockItem item)
    {
        if (string.IsNullOrEmpty(item.Icon)) return null;
        string path = System.IO.Path.IsPathRooted(item.Icon) ? item.Icon : System.IO.Path.Combine(Dir, item.Icon);
        return LoadFile(path);
    }

    /// <summary>Miniature décodée à petite taille (pour le sélecteur, qui en affiche des centaines).</summary>
    public static ImageSource? LoadThumb(string path, int pixels)
    {
        try
        {
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.UriSource = new Uri(path);
            bi.DecodePixelWidth = pixels;
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bi.EndInit();
            bi.Freeze();
            return bi;
        }
        catch { return null; }
    }

    public static ImageSource? LoadFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var stamp = File.GetLastWriteTimeUtc(path);
            if (Cache.TryGetValue(path, out var hit) && hit.Stamp == stamp) return hit.Image;

            var decoder = BitmapDecoder.Create(new Uri(path), BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames.OrderByDescending(f => f.PixelWidth).First(); // .ico : la plus grande taille
            frame.Freeze();
            Cache[path] = (stamp, frame);
            return frame;
        }
        catch { return null; }
    }
}
