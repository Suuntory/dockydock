using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Media;

namespace DockyDock;

public sealed class ShadowSpec
{
    public string Color { get; set; } = "#000000";
    public double Opacity { get; set; } = 0.35;
    public double BlurRadius { get; set; } = 16;
    public double OffsetY { get; set; } = 3;
}

/// <summary>
/// Apparence du dock. Chargé depuis un fichier JSON de %AppData%\DockyDock\themes.
/// Les dimensions (padding, gap, rayon...) sont exprimées pour une icône de <see cref="ReferenceIconSize"/> px
/// et suivent proportionnellement la taille d'icône choisie dans les paramètres.
/// </summary>
public sealed class Theme
{
    public string Name { get; set; } = "Sans nom";
    public double ReferenceIconSize { get; set; } = 52;

    public double PaddingX { get; set; } = 12;
    public double PaddingY { get; set; } = 8;
    public double Gap { get; set; } = 6;
    public double CornerRadius { get; set; } = 18;

    /// <summary>Opacité du fond : 0 = dock invisible (les icônes restent), 1 = couleur pleine. Remplace le canal alpha de Background.</summary>
    public double Opacity { get; set; } = 0.63;
    /// <summary>Couleur du fond (#RRGGBB, l'alpha vient de Opacity). Avec BackgroundBottom, dégradé vertical.</summary>
    public string Background { get; set; } = "#3A3A40";
    public string? BackgroundBottom { get; set; }
    public string BorderColor { get; set; } = "#50FFFFFF";
    public double BorderThickness { get; set; } = 1;
    /// <summary>Second liseré, à l'intérieur du premier (reflet).</summary>
    public string? InnerBorderColor { get; set; }
    public ShadowSpec? Shadow { get; set; }

    public string SeparatorColor { get; set; } = "#40FFFFFF";

    public string DotColor { get; set; } = "#D9FFFFFF";
    public double DotSize { get; set; } = 4;

    public string LabelBackground { get; set; } = "#E62A2A2E";
    public string LabelForeground { get; set; } = "#FFFFFF";
    public string? LabelBorderColor { get; set; }
    public double LabelFontSize { get; set; } = 13;
    public double LabelCornerRadius { get; set; } = 7;
    public string? FontFamily { get; set; }

    public static Color ParseColor(string? s, Color fallback)
    {
        try { return s == null ? fallback : (Color)ColorConverter.ConvertFromString(s); }
        catch { return fallback; }
    }
}

public static class ThemeManager
{
    public const string DefaultName = "First";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string Dir => Path.Combine(DockConfig.Dir, "themes");

    /// <summary>Écrit (ou réécrit) les thèmes fournis avec l'appli. Pour un thème perso, copier un fichier et changer son "name".</summary>
    public static void EnsureBuiltIns()
    {
        Directory.CreateDirectory(Dir);
        foreach (var t in new[] { First(), Mak() })
            File.WriteAllText(Path.Combine(Dir, t.Name + ".json"), JsonSerializer.Serialize(t, Json));
    }

    public static List<Theme> LoadAll()
    {
        var themes = new List<Theme>();
        if (Directory.Exists(Dir))
            foreach (var file in Directory.GetFiles(Dir, "*.json"))
            {
                try
                {
                    var t = JsonSerializer.Deserialize<Theme>(File.ReadAllText(file), Json);
                    if (t != null) themes.Add(t);
                }
                catch { /* thème invalide : ignoré */ }
            }
        if (themes.Count == 0) themes.Add(First());
        return themes.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static Theme Get(string name)
    {
        var all = LoadAll();
        return all.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? all.FirstOrDefault(t => t.Name == DefaultName)
            ?? all[0];
    }

    private static Theme First() => new() { Name = "First" };

    // Mesuré sur la capture du dock macOS : pilule grise translucide, rayon ~10px pour des icônes de 38px,
    // liseré sombre à l'extérieur, léger reflet clair à l'intérieur, point d'appli ouverte discret.
    private static Theme Mak() => new()
    {
        Name = "Mak",
        ReferenceIconSize = 38,
        PaddingX = 10,
        PaddingY = 9,
        Gap = 6,
        CornerRadius = 10,
        Opacity = 0.94,
        Background = "#404148",
        BackgroundBottom = "#3C3D43",
        BorderColor = "#30000000",
        BorderThickness = 1,
        InnerBorderColor = "#18FFFFFF",
        DotColor = "#E6FFFFFF",
        DotSize = 3.5,
        LabelBackground = "#E6323236",
        LabelForeground = "#FFFFFF",
        LabelBorderColor = "#22FFFFFF",
        LabelFontSize = 13,
        LabelCornerRadius = 7,
        FontFamily = "Segoe UI",
    };
}
