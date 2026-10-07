using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace DockyDock;

public sealed class DockItem
{
    public string Path { get; set; } = "";
    /// <summary>Nom affiché (utile pour les éléments shell qui n'ont pas de nom de fichier).</summary>
    public string? Name { get; set; }
    /// <summary>Icône personnalisée : nom d'un fichier du dossier "icons" (ou chemin complet). null = icône de l'appli.</summary>
    public string? Icon { get; set; }
}

public sealed class DockConfig
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>
    /// %UserProfile%\.dockydock. Volontairement hors de %AppData% : lancé depuis une appli packagée (MSIX),
    /// un programme voit un %AppData% virtualisé, donc une config différente de celle d'un lancement normal.
    /// </summary>
    public static string Dir { get; } = ResolveDir();

    private static string ResolveDir()
    {
        string dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dockydock");
        Directory.CreateDirectory(dir);

        // Reprise de l'ancien emplacement (%AppData%\DockyDock) au premier lancement.
        string legacy = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DockyDock");
        string legacyConfig = System.IO.Path.Combine(legacy, "config.json");
        if (!File.Exists(System.IO.Path.Combine(dir, "config.json")) && File.Exists(legacyConfig))
        {
            try { File.Copy(legacyConfig, System.IO.Path.Combine(dir, "config.json")); } catch { }
            try
            {
                string legacyThemes = System.IO.Path.Combine(legacy, "themes");
                if (Directory.Exists(legacyThemes))
                {
                    string themes = System.IO.Path.Combine(dir, "themes");
                    Directory.CreateDirectory(themes);
                    foreach (var f in Directory.GetFiles(legacyThemes, "*.json"))
                        File.Copy(f, System.IO.Path.Combine(themes, System.IO.Path.GetFileName(f)), true);
                }
            }
            catch { }
        }
        return dir;
    }
    public static string FilePath => System.IO.Path.Combine(Dir, "config.json");

    /// <summary>-1 = écran principal, sinon index dans Screen.AllScreens.</summary>
    public int Monitor { get; set; } = -1;
    public double IconSize { get; set; } = 52;
    public double Zoom { get; set; } = 1.7;
    /// <summary>Distance entre le bas de la pilule et le bas de l'écran, en px (0 = collé au bord).</summary>
    public double VerticalOffset { get; set; } = 6;
    /// <summary>Nom d'un thème de %AppData%\DockyDock\themes.</summary>
    public string Theme { get; set; } = "First";
    /// <summary>Remplace l'opacité du thème (0 à 1) ; null = valeur du thème.</summary>
    public double? Opacity { get; set; }
    /// <summary>"maximized" (masqué sous une fenêtre maximisée), "always" ou "never".</summary>
    public string HideMode { get; set; } = "maximized";
    public List<DockItem> Items { get; set; } = new();

    public static DockConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<DockConfig>(File.ReadAllText(FilePath)) ?? Default();
        }
        catch { /* config illisible : on repart sur les valeurs par défaut */ }
        var cfg = Default();
        cfg.Save();
        return cfg;
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
    }

    private static DockConfig Default()
    {
        string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var cfg = new DockConfig();
        foreach (var p in new[]
        {
            System.IO.Path.Combine(win, "explorer.exe"),
            System.IO.Path.Combine(win, "notepad.exe"),
            System.IO.Path.Combine(win, "System32", "calc.exe"),
            System.IO.Path.Combine(win, "System32", "cmd.exe"),
        })
            if (File.Exists(p)) cfg.Items.Add(new DockItem { Path = p });
        return cfg;
    }
}
