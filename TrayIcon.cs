using System;
using System.Drawing;
using System.Windows.Forms;

namespace DockyDock;

internal sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;

    public TrayIcon(DockWindow dock, System.Windows.Application app)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Paramètres…", null, (_, _) => dock.ShowSettings());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quitter", null, (_, _) => app.Shutdown());

        Icon? icon = null;
        try { icon = Environment.ProcessPath is { } p ? Icon.ExtractAssociatedIcon(p) : null; } catch { }
        _icon = new NotifyIcon
        {
            Icon = icon ?? SystemIcons.Application,
            Text = "DockyDock",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _icon.DoubleClick += (_, _) => dock.ShowSettings();
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
