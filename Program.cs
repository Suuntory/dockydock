using System;
using System.Threading;
using System.Windows;

namespace DockyDock;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(true, "DockyDock.SingleInstance", out bool created);
        if (!created) return;

        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var cfg = DockConfig.Load();
        ThemeManager.EnsureBuiltIns();
        var dock = new DockWindow(cfg);
        using var tray = new TrayIcon(dock, app);
        dock.Show();
        app.Run();
    }
}
