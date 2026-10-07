using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace DockyDock;

internal enum ResultKind { App, File, Calc, Url, Web }

internal sealed class SearchResult
{
    public ResultKind Kind;
    public string Title = "";
    public string Subtitle = "";
    /// <summary>Ce qu'on lance : chemin, "shell:AppsFolder\..." ou URL.</summary>
    public string Target = "";
    /// <summary>Chemin à passer à IconLoader pour l'icône (null = pictogramme).</summary>
    public string? IconPath;
    /// <summary>Texte copié pour un résultat de calcul.</summary>
    public string? Copy;
    public int Score;
}

/// <summary>Nombre de lancements par appli, pour remonter les plus utilisées dans les résultats.</summary>
internal static class SearchUsage
{
    private static Dictionary<string, int>? _counts;
    private static string FilePath => Path.Combine(DockConfig.Dir, "search-usage.json");

    private static Dictionary<string, int> Counts => _counts ??= Load();

    private static Dictionary<string, int> Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(FilePath)) ?? new();
        }
        catch { }
        return new();
    }

    public static int Get(string target) => Counts.TryGetValue(target, out var n) ? n : 0;

    public static void Increment(string target)
    {
        Counts[target] = Get(target) + 1;
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(Counts)); } catch { }
    }
}

/// <summary>Applications installées : la liste "Toutes les applications" du menu Démarrer (dossier shell:AppsFolder).</summary>
internal sealed class AppIndex
{
    private sealed record Entry(string Name, string Lower, string Target);

    private volatile List<Entry> _entries = new();
    private DateTime _built = DateTime.MinValue;
    private int _building;

    /// <summary>Reconstruit l'index en tâche de fond (au plus toutes les 5 minutes, sauf force).</summary>
    public void RefreshAsync(bool force = false)
    {
        if (!force && DateTime.UtcNow - _built < TimeSpan.FromMinutes(5)) return;
        if (Interlocked.Exchange(ref _building, 1) == 1) return;

        // COM du shell : thread STA dédié.
        var thread = new Thread(() =>
        {
            try
            {
                var list = ReadAppsFolder();
                if (list.Count < 20) AddStartMenuShortcuts(list);
                _entries = list;
                _built = DateTime.UtcNow;
            }
            catch { }
            finally { Interlocked.Exchange(ref _building, 0); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    private static List<Entry> ReadAppsFolder()
    {
        var list = new List<Entry>();
        var type = Type.GetTypeFromProgID("Shell.Application");
        if (type == null) return list;
        dynamic shell = Activator.CreateInstance(type)!;
        dynamic folder = shell.NameSpace("shell:AppsFolder");
        dynamic items = folder.Items();
        int count = items.Count;
        for (int i = 0; i < count; i++)
        {
            try
            {
                dynamic item = items.Item(i);
                string name = item.Name;
                string path = item.Path;
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(path)) continue;
                list.Add(new Entry(name, name.ToLowerInvariant(), @"shell:AppsFolder\" + path));
            }
            catch { }
        }
        return list;
    }

    private static void AddStartMenuShortcuts(List<Entry> list)
    {
        var known = new HashSet<string>(list.Select(e => e.Lower));
        foreach (var root in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
        })
        {
            if (!Directory.Exists(root)) continue;
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "Programs"), "*.lnk", SearchOption.AllDirectories))
            {
                string name = Path.GetFileNameWithoutExtension(file);
                if (known.Add(name.ToLowerInvariant())) list.Add(new Entry(name, name.ToLowerInvariant(), file));
            }
        }
    }

    public List<SearchResult> Query(string query, int max)
    {
        string q = query.ToLowerInvariant();
        var hits = new List<SearchResult>();
        foreach (var e in _entries)
        {
            int score = Score(e.Lower, q);
            if (score <= 0) continue;
            score += Math.Min(30, SearchUsage.Get(e.Target) * 3);
            hits.Add(new SearchResult
            {
                Kind = ResultKind.App, Title = e.Name, Subtitle = "Application",
                Target = e.Target, IconPath = e.Target, Score = score,
            });
        }
        return hits.OrderByDescending(h => h.Score).ThenBy(h => h.Title.Length).Take(max).ToList();
    }

    private static int Score(string name, string q)
    {
        if (name == q) return 120;
        if (name.StartsWith(q, StringComparison.Ordinal)) return 100;
        int idx = name.IndexOf(q, StringComparison.Ordinal);
        if (idx > 0 && !char.IsLetterOrDigit(name[idx - 1])) return 80;  // début d'un mot
        if (idx > 0) return 50;
        // initiales : "vsc" -> "Visual Studio Code"
        var initials = new string(name.Split(' ', '-', '_', '.').Where(w => w.Length > 0).Select(w => w[0]).ToArray());
        return q.Length >= 2 && initials.StartsWith(q, StringComparison.Ordinal) ? 60 : 0;
    }
}

/// <summary>Fichiers et dossiers via l'index de Windows Search (le même que la recherche de Windows).</summary>
internal static class FileSearch
{
    public static List<SearchResult> Query(string term, int max)
    {
        var results = new List<SearchResult>();
        // LIKE : on retire les jokers saisis par l'utilisateur. (CONTAINS ne renvoie rien sur certaines machines.)
        string safe = term.Replace("'", "''").Replace("\"", "").Replace("%", "").Replace("_", "").Replace("[", "").Replace("]", "").Trim();
        if (safe.Length < 2) return results;

        var hits = new List<(string Name, string Display, string Path)>();
        dynamic conn = null!, rs = null!;
        try
        {
            conn = Activator.CreateInstance(Type.GetTypeFromProgID("ADODB.Connection")!)!;
            conn.Open("Provider=Search.CollatorDSO;Extended Properties='Application=Windows'");
            rs = Activator.CreateInstance(Type.GetTypeFromProgID("ADODB.Recordset")!)!;
            // ItemUrl = vrai chemin ; ItemPathDisplay est traduit (dossier "Utilisateurs"...) et ne se lance pas.
            string sql = "SELECT TOP 60 System.ItemName, System.ItemPathDisplay, System.ItemUrl FROM SystemIndex " +
                         $"WHERE scope='file:' AND System.ItemName LIKE '%{safe}%'";
            rs.Open(sql, conn);
            while (!rs.EOF && hits.Count < 60)
            {
                string name = Convert.ToString(rs.Fields.Item(0).Value) ?? "";
                string display = Convert.ToString(rs.Fields.Item(1).Value) ?? "";
                string url = Convert.ToString(rs.Fields.Item(2).Value) ?? "";
                rs.MoveNext();
                if (name.Length == 0 || !url.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) continue;
                string path = url.Substring(5).Replace('/', '\\');
                // Les raccourcis du menu Démarrer sont déjà dans les applis ; AppData n'intéresse personne.
                if (path.Contains(@"\AppData\", StringComparison.OrdinalIgnoreCase) ||
                    path.Contains(@"\Start Menu\", StringComparison.OrdinalIgnoreCase)) continue;
                hits.Add((name, display, path));
            }
        }
        catch { /* Windows Search désactivé ou indisponible : pas de fichiers */ }
        finally
        {
            try { rs.Close(); } catch { }
            try { conn.Close(); } catch { }
        }

        // Noms qui commencent par le texte d'abord, puis les plus courts.
        foreach (var h in hits
                     .OrderBy(h => h.Name.StartsWith(safe, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                     .ThenBy(h => h.Name.Length)
                     .Take(max))
        {
            results.Add(new SearchResult
            {
                Kind = ResultKind.File, Title = h.Name, Target = h.Path, IconPath = h.Path,
                Subtitle = Path.GetDirectoryName(h.Display) ?? h.Display,
            });
        }
        return results;
    }
}

internal static class Calculator
{
    /// <summary>Évalue une expression arithmétique simple ("12*(3+4)", "10,5/2"). Renvoie null si ce n'en est pas une.</summary>
    public static string? TryEvaluate(string input)
    {
        string s = input.Trim().Replace(',', '.').Replace('×', '*').Replace('x', '*').Replace('÷', '/').Replace(" ", "");
        if (s.Length < 3 || !s.Any(char.IsDigit) || !s.Any(c => "+-*/%".Contains(c))) return null;
        if (!s.All(c => char.IsDigit(c) || "+-*/%().".Contains(c))) return null;
        try
        {
            object value = new DataTable().Compute(s, "");
            double d = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            if (double.IsNaN(d) || double.IsInfinity(d)) return null;
            return d.ToString("0.##########", CultureInfo.CurrentCulture);
        }
        catch { return null; }
    }
}
