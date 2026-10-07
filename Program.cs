using System;
using System.Linq;
using System.Threading;
using System.Windows;

namespace DockyDock;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        using var mutex = new Mutex(true, "DockyDock.SingleInstance", out bool created);
        if (!created) return;

        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.ThemeMode = ThemeMode.Dark; // thème Fluent sombre de WPF : fenêtres, menus, onglets, curseurs...
        var cfg = DockConfig.Load();
        ThemeManager.EnsureBuiltIns();
        var dock = new DockWindow(cfg);
        using var search = new SearchFeature(cfg, app.Dispatcher);
        using var tray = new TrayIcon(dock, app);
        dock.Show();
        search.Apply();
        if (args.Contains("--settings")) app.Dispatcher.BeginInvoke(new Action(dock.ShowSettings), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        if (args.Contains("--search")) app.Dispatcher.BeginInvoke(new Action(search.Toggle), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        app.Run();
    }
}
