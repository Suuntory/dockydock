using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace DockyDock;

public sealed class DockWindow : Window
{
    private const string DragFormat = "dockydock-item";
    private const string TransientFormat = "dockydock-transient";
    private const string ShellIdListFormat = "Shell IDList Array";
    private const double LabelRoom = 44;

    // Métriques issues du thème, mises à l'échelle de la taille d'icône (voir ApplyTheme).
    private double PadX = 12, PadY = 8, Gap = 6, BottomMargin = 6, _dotSize = 4;
    private Theme _theme = new();
    private SettingsWindow? _settings;

    private sealed class DockIcon
    {
        public DockItem Item = null!;
        public Image Image = null!;
        public Ellipse Dot = null!;
        public string? ExePath;
        public string Name = "";
        public double Scale = 1;
        public IntPtr Hwnd;
        public bool Transient, Uwp;
    }

    /// <summary>Appli ouverte mais non épinglée, affichée à droite du dock.</summary>
    private sealed class Transient
    {
        public string Key = "", Path = "", Name = "";
        public IntPtr Hwnd;
        public bool Uwp;
        public ImageSource? Icon;
    }

    private sealed record WinInfo(string Path, IntPtr Hwnd, string Title, bool Uwp);

    private readonly DockConfig _cfg;
    private readonly Canvas _canvas = new();
    private readonly Border _pill = new();
    private readonly Border _pillInner = new();
    private readonly Rectangle _sep = new() { Width = 1, IsHitTestVisible = false };
    private readonly List<Transient> _transients = new();
    private readonly Dictionary<string, string> _appNames = new(StringComparer.OrdinalIgnoreCase);
    private int _pinnedCount;
    private readonly Border _label = new();
    private readonly TextBlock _labelText = new();
    private readonly TranslateTransform _slide = new();
    private readonly List<DockIcon> _icons = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly DispatcherTimer _tick = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly DispatcherTimer _runningTimer = new() { Interval = TimeSpan.FromSeconds(2) };

    private IntPtr _hwnd;
    private IntPtr _monitor;
    private Native.RECT _monPx, _winPx;
    private double _scale = 1, _w = 200, _h = 100, _pillRestH;
    private double _hideProgress;
    private bool _wantHidden, _layoutDirty = true, _lastCovers;
    private long _lastEval, _lastActive;
    private int _menuOpen;
    private ContextMenu? _openMenu;
    private bool _dragging, _suppressClick, _dragCancelled;
    private DockIcon? _pressed;
    private Point _dragOrigin;

    public DockWindow(DockConfig cfg)
    {
        _cfg = cfg;

        Title = "DockyDock";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        AllowDrop = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Width = 200;
        Height = 100;

        _pill.AllowDrop = true;
        _pill.Child = _pillInner;
        var pillMenu = NewMenu();
        AddSettingsAndQuit(pillMenu);
        _pill.ContextMenu = pillMenu;

        _label.Child = _labelText;
        _label.Padding = new Thickness(10, 4, 10, 4);
        _label.Visibility = Visibility.Collapsed;
        _label.IsHitTestVisible = false;

        _canvas.RenderTransform = _slide;
        Content = _canvas;

        DragOver += OnDragOver;
        Drop += OnDrop;
        _tick.Tick += (_, _) => Tick();
        _runningTimer.Tick += async (_, _) => await PollRunning();
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        int ex = Native.GetWindowLong(_hwnd, Native.GWL_EXSTYLE);
        Native.SetWindowLong(_hwnd, Native.GWL_EXSTYLE, ex | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE);
        Rebuild();
        _lastActive = _clock.ElapsedMilliseconds;
        _tick.Start();
        _runningTimer.Start();
    }

    protected override void OnClosed(EventArgs e)
    {
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        base.OnClosed(e);
    }

    private void OnDisplayChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(ApplyLayout);

    // ---------- construction ----------

    /// <summary>Reconstruit les icônes depuis la config et replace la fenêtre.</summary>
    public void Rebuild()
    {
        ApplyTheme();
        _canvas.Children.Clear();
        _icons.Clear();
        _canvas.Children.Add(_pill);
        _canvas.Children.Add(_sep);

        var entries = _cfg.Items.Select(i => (Item: i, T: (Transient?)null))
            .Concat(_transients.Select(t => (Item: new DockItem { Path = t.Path, Name = t.Name }, T: (Transient?)t)))
            .ToList();
        _pinnedCount = _cfg.Items.Count;

        foreach (var (item, tr) in entries)
        {
            var icon = new DockIcon
            {
                Item = item,
                Name = item.Name ?? System.IO.Path.GetFileNameWithoutExtension(item.Path),
                ExePath = tr == null ? ResolveExe(item.Path) : null,
                Transient = tr != null,
                Uwp = tr?.Uwp ?? false,
                Hwnd = tr?.Hwnd ?? IntPtr.Zero,
            };
            icon.Image = new Image { Source = tr?.Icon ?? IconLibrary.LoadFor(item) ?? IconLoader.Load(item.Path), AllowDrop = true };
            RenderOptions.SetBitmapScalingMode(icon.Image, BitmapScalingMode.HighQuality);
            icon.Dot = new Ellipse
            {
                Width = _dotSize, Height = _dotSize,
                Fill = new SolidColorBrush(Theme.ParseColor(_theme.DotColor, Colors.White)),
                Visibility = tr != null ? Visibility.Visible : Visibility.Collapsed, IsHitTestVisible = false,
            };
            WireIcon(icon);
            _icons.Add(icon);
            _canvas.Children.Add(icon.Image);
            _canvas.Children.Add(icon.Dot);
        }
        _canvas.Children.Add(_label);

        ApplyLayout();
        _ = PollRunning();
    }

    /// <summary>Met à jour l'image d'un élément du dock sans tout reconstruire (aperçu d'icône).</summary>
    private void RefreshIcon(DockItem item)
    {
        var ic = _icons.FirstOrDefault(i => ReferenceEquals(i.Item, item));
        if (ic != null) ic.Image.Source = IconLibrary.LoadFor(item) ?? IconLoader.Load(item.Path);
    }

    // ---------- icône "fantôme" qui suit la souris pendant un glisser ----------

    private Window? _ghost;
    private IntPtr _ghostHwnd;
    private DockIcon? _ghostSource;

    private void StartGhost(DockIcon icon)
    {
        double size = _cfg.IconSize * 1.1;
        var ghost = new Window
        {
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            Focusable = false,
            Width = size,
            Height = size,
            Content = new Image { Source = icon.Image.Source, Stretch = Stretch.Uniform },
        };
        _ghostHwnd = new WindowInteropHelper(ghost).EnsureHandle();
        // Transparent aux clics : sinon le fantôme, sous le curseur, masquerait le dock comme cible de dépôt.
        int ex = Native.GetWindowLong(_ghostHwnd, Native.GWL_EXSTYLE);
        Native.SetWindowLong(_ghostHwnd, Native.GWL_EXSTYLE, ex | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TRANSPARENT);
        _ghost = ghost;
        _ghostSource = icon;
        icon.Image.Opacity = 0.3;
        ghost.Show();
        UpdateGhost();
    }

    private void UpdateGhost()
    {
        if (_ghost == null) return;
        Native.GetCursorPos(out var pt);
        int px = (int)Math.Round(_cfg.IconSize * 1.1 * _scale);
        Native.SetWindowPos(_ghostHwnd, Native.HWND_TOPMOST, pt.X - px / 2, pt.Y - px / 2, px, px, Native.SWP_NOACTIVATE);
        // Une icône épinglée lâchée hors du dock est retirée : on l'estompe pour le signaler.
        _ghost.Opacity = _ghostSource is { Transient: false } && !IsOverDock(pt) ? 0.5 : 1;
    }

    private void EndGhost()
    {
        _ghost?.Close();
        _ghost = null;
        if (_ghostSource != null) _ghostSource.Image.Opacity = 1;
        _ghostSource = null;
    }

    private void ApplyTheme()
    {
        var t = _theme = ThemeManager.Get(_cfg.Theme);
        double k = _cfg.IconSize / Math.Max(1, t.ReferenceIconSize);
        PadX = t.PaddingX * k;
        PadY = t.PaddingY * k;
        Gap = t.Gap * k;
        BottomMargin = Math.Max(0, _cfg.VerticalOffset);
        _dotSize = Math.Max(2, t.DotSize * k);
        double radius = t.CornerRadius * k;
        double border = t.BorderThickness > 0 ? Math.Max(1, t.BorderThickness * Math.Min(k, 1.5)) : 0;

        // Opacité : 0 = pilule invisible (bordures et ombre comprises), 1 = fond plein.
        double opacity = Math.Clamp(_cfg.Opacity ?? t.Opacity, 0, 1);
        double fade = t.Opacity <= 0 ? opacity : Math.Min(1, opacity / t.Opacity);
        static Color WithAlpha(Color c, double a) => Color.FromArgb((byte)Math.Round(Math.Clamp(a, 0, 1) * 255), c.R, c.G, c.B);
        Color Faded(string? col) { var c = Theme.ParseColor(col, Colors.Transparent); return WithAlpha(c, c.A / 255.0 * fade); }

        var top = WithAlpha(Theme.ParseColor(t.Background, Color.FromRgb(0x3A, 0x3A, 0x40)), opacity);
        _pill.Background = t.BackgroundBottom == null
            ? new SolidColorBrush(top)
            : new LinearGradientBrush(top, WithAlpha(Theme.ParseColor(t.BackgroundBottom, top), opacity), 90);
        _pill.BorderBrush = new SolidColorBrush(Faded(t.BorderColor));
        _pill.BorderThickness = new Thickness(border);
        _pill.CornerRadius = new CornerRadius(radius);
        _pillInner.CornerRadius = new CornerRadius(Math.Max(0, radius - border));
        _pillInner.BorderThickness = new Thickness(t.InnerBorderColor == null ? 0 : 1);
        _pillInner.BorderBrush = new SolidColorBrush(Faded(t.InnerBorderColor));
        _pill.Effect = t.Shadow == null ? null : new System.Windows.Media.Effects.DropShadowEffect
        {
            Color = Theme.ParseColor(t.Shadow.Color, Colors.Black),
            Opacity = t.Shadow.Opacity * fade,
            BlurRadius = t.Shadow.BlurRadius * k,
            ShadowDepth = 0,
            Direction = 270,
        };
        if (t.Shadow != null && t.Shadow.OffsetY != 0)
            ((System.Windows.Media.Effects.DropShadowEffect)_pill.Effect!).ShadowDepth = Math.Abs(t.Shadow.OffsetY) * k;

        _sep.Fill = new SolidColorBrush(Faded(t.SeparatorColor));
        _label.Background = new SolidColorBrush(Theme.ParseColor(t.LabelBackground, Color.FromArgb(0xE6, 0x2A, 0x2A, 0x2E)));
        _label.BorderBrush = new SolidColorBrush(Theme.ParseColor(t.LabelBorderColor, Colors.Transparent));
        _label.BorderThickness = new Thickness(t.LabelBorderColor == null ? 0 : 1);
        _label.CornerRadius = new CornerRadius(t.LabelCornerRadius);
        _labelText.Foreground = new SolidColorBrush(Theme.ParseColor(t.LabelForeground, Colors.White));
        _labelText.FontSize = t.LabelFontSize;
        _labelText.FontFamily = t.FontFamily != null ? new FontFamily(t.FontFamily) : SystemFonts.MessageFontFamily;
    }

    public void ShowSettings()
    {
        if (_settings != null) { _settings.Activate(); return; }
        _settings = new SettingsWindow(_cfg, this);
        _settings.Closed += (_, _) => _settings = null;
        _settings.Show();
    }

    private void WireIcon(DockIcon icon)
    {
        var img = icon.Image;
        img.MouseLeftButtonDown += (_, e) =>
        {
            _pressed = icon;
            _dragOrigin = e.GetPosition(this);
            _suppressClick = false;
        };
        img.MouseMove += (_, e) =>
        {
            if (e.LeftButton != MouseButtonState.Pressed || _pressed != icon || (icon.Transient && icon.Uwp)) return;
            var p = e.GetPosition(this);
            if (Math.Abs(p.X - _dragOrigin.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(p.Y - _dragOrigin.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            _pressed = null;
            _suppressClick = true;
            _dragging = true;
            _dragCancelled = false;
            // Une appli non épinglée glissée sur le dock s'y épingle ; une icône épinglée se réordonne.
            var data = icon.Transient ? new DataObject(TransientFormat, icon.Item.Path) : new DataObject(DragFormat, _icons.IndexOf(icon));
            StartGhost(icon);
            try { DragDrop.DoDragDrop(img, data, DragDropEffects.Move); }
            finally { _dragging = false; EndGhost(); }

            // Lâché hors du dock (et pas annulé avec Échap) : on retire l'icône.
            Native.GetCursorPos(out var drop);
            if (!_dragCancelled && !IsOverDock(drop) && _cfg.Items.Remove(icon.Item))
            {
                _cfg.Save();
                Dispatcher.BeginInvoke(Rebuild);
            }
        };
        img.QueryContinueDrag += (_, e) =>
        {
            if (e.EscapePressed) _dragCancelled = true;
            UpdateGhost();
        };
        // Le fantôme remplace le curseur de glisser par défaut ; on garde juste l'indication "interdit".
        img.GiveFeedback += (_, e) =>
        {
            e.UseDefaultCursors = false;
            Mouse.SetCursor(e.Effects == DragDropEffects.None ? Cursors.No : Cursors.Arrow);
            e.Handled = true;
        };
        img.MouseLeftButtonUp += (_, _) =>
        {
            if (_suppressClick) { _suppressClick = false; return; }
            if (_pressed == icon) OnIconClick(icon);
            _pressed = null;
        };

        var menu = NewMenu();
        if (icon.Transient)
        {
            if (!icon.Uwp)
            {
                var pin = new MenuItem { Header = "Épingler au dock" };
                pin.Click += (_, _) =>
                {
                    _cfg.Items.Add(new DockItem { Path = icon.Item.Path });
                    _cfg.Save();
                    _transients.RemoveAll(t => t.Path.Equals(icon.Item.Path, StringComparison.OrdinalIgnoreCase));
                    Rebuild();
                };
                menu.Items.Add(pin);
            }
            AddSettingsAndQuit(menu, leadingSeparator: !icon.Uwp);
            AddQuitApp(menu, icon);
            img.ContextMenu = menu;
            return;
        }
        var open = new MenuItem { Header = "Ouvrir" };
        open.Click += (_, _) => Launch(icon);
        var reveal = new MenuItem { Header = "Afficher dans l'Explorateur" };
        reveal.Click += (_, _) => Process.Start("explorer.exe", $"/select,\"{icon.Item.Path}\"");
        var changeIcon = new MenuItem { Header = "Changer l'icône…" };
        changeIcon.Click += (_, _) =>
        {
            var item = icon.Item;
            var picker = new IconPickerWindow(icon.Name, item.Icon,
                onPreview: file => { item.Icon = file; RefreshIcon(item); },
                onCommit: file => { item.Icon = file; _cfg.Save(); RefreshIcon(item); });
            picker.Show();
        };
        var remove = new MenuItem { Header = "Retirer du dock" };
        remove.Click += (_, _) => { _cfg.Items.Remove(icon.Item); _cfg.Save(); Rebuild(); };
        menu.Items.Add(open);
        menu.Items.Add(reveal);
        menu.Items.Add(changeIcon);
        menu.Items.Add(new Separator());
        menu.Items.Add(remove);
        AddSettingsAndQuit(menu);
        AddQuitApp(menu, icon);
        img.ContextMenu = menu;
    }

    private ContextMenu NewMenu()
    {
        var m = new ContextMenu();
        m.Opened += (_, _) => { _menuOpen++; _openMenu = m; };
        m.Closed += (_, _) =>
        {
            _menuOpen = Math.Max(0, _menuOpen - 1);
            if (_openMenu == m) _openMenu = null;
        };
        return m;
    }

    /// <summary>"Quitter &lt;appli&gt;" tout en bas du menu : visible seulement si l'appli est ouverte.</summary>
    private void AddQuitApp(ContextMenu menu, DockIcon icon)
    {
        var separator = new Separator { Visibility = Visibility.Collapsed };
        var quit = new MenuItem { Header = $"Quitter {icon.Name}", Visibility = Visibility.Collapsed };
        quit.Click += (_, _) => CloseApp(icon);
        menu.Items.Add(separator);
        menu.Items.Add(quit);
        menu.Opened += (_, _) =>
            separator.Visibility = quit.Visibility = icon.Hwnd != IntPtr.Zero ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Ferme proprement (WM_CLOSE) toutes les fenêtres de l'appli.</summary>
    private static void CloseApp(DockIcon icon)
    {
        string? exe = icon.Transient ? icon.Item.Path : icon.ExePath;
        var targets = ScanWindows()
            .Where(w => icon.Uwp ? w.Hwnd == icon.Hwnd : !w.Uwp && exe != null && string.Equals(w.Path, exe, StringComparison.OrdinalIgnoreCase))
            .Select(w => w.Hwnd)
            .ToList();
        if (targets.Count == 0 && icon.Hwnd != IntPtr.Zero) targets.Add(icon.Hwnd);
        foreach (var hwnd in targets) Native.PostMessage(hwnd, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
    }

    private void AddSettingsAndQuit(ContextMenu menu, bool leadingSeparator = true)
    {
        var settings = new MenuItem { Header = "Paramètres…" };
        settings.Click += (_, _) => ShowSettings();
        var quit = new MenuItem { Header = "Quitter DockyDock" };
        quit.Click += (_, _) => System.Windows.Application.Current.Shutdown();
        if (leadingSeparator) menu.Items.Add(new Separator());
        menu.Items.Add(settings);
        menu.Items.Add(quit);
    }

    // La fenêtre est NOACTIVATE : WPF ne détecte donc pas les clics hors du menu, on s'en charge.
    private void CloseMenuOnOutsideClick(Native.POINT pt)
    {
        if (_openMenu == null) return;
        bool down = ((Native.GetAsyncKeyState(0x01) | Native.GetAsyncKeyState(0x02) | Native.GetAsyncKeyState(0x04)) & 0x8000) != 0;
        if (!down) return;
        var under = Native.WindowFromPoint(pt);
        var menuHwnd = (PresentationSource.FromVisual(_openMenu) as HwndSource)?.Handle ?? IntPtr.Zero;
        if (menuHwnd != IntPtr.Zero && (under == menuHwnd || Native.GetAncestor(under, 2) == menuHwnd)) return;
        _openMenu.IsOpen = false;
    }

    private bool IsOverDock(Native.POINT pt)
    {
        double mx = (pt.X - _winPx.Left) / _scale, my = (pt.Y - _winPx.Top) / _scale;
        double left = Canvas.GetLeft(_pill), pillTop = _h - BottomMargin - _pillRestH;
        return mx >= left - 20 && mx <= left + _pill.Width + 20 && my >= pillTop - 24;
    }

    // ---------- placement ----------

    private System.Windows.Forms.Screen PickScreen()
    {
        var all = System.Windows.Forms.Screen.AllScreens;
        if (_cfg.Monitor >= 0 && _cfg.Monitor < all.Length) return all[_cfg.Monitor];
        return System.Windows.Forms.Screen.PrimaryScreen ?? all[0];
    }

    public void ApplyLayout()
    {
        if (_hwnd == IntPtr.Zero) return;

        var screen = PickScreen();
        var b = screen.Bounds;
        _monPx = new Native.RECT { Left = b.Left, Top = b.Top, Right = b.Right, Bottom = b.Bottom };
        _monitor = Native.MonitorFromPoint(new Native.POINT { X = b.Left + b.Width / 2, Y = b.Top + b.Height / 2 }, Native.MONITOR_DEFAULTTONEAREST);

        // On se place d'abord sur l'écran cible pour obtenir le bon DPI.
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, b.Left, b.Top, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        _scale = Native.GetDpiForWindow(_hwnd) / 96.0;

        double size = _cfg.IconSize, zoom = _cfg.Zoom;
        int n = Math.Max(1, _icons.Count);
        double restTotal = n * size + (n - 1) * Gap + SepExtra;
        _pillRestH = size + 2 * PadY;
        _w = Math.Min(restTotal + 2 * PadX + (zoom - 1) * size * 4 + 40, b.Width / _scale - 20);
        _h = _pillRestH + (zoom - 1) * size + BottomMargin + LabelRoom;
        _canvas.Width = _w;
        _canvas.Height = _h;

        int pw = (int)Math.Round(_w * _scale), ph = (int)Math.Round(_h * _scale);
        int x = b.Left + (b.Width - pw) / 2;
        int y = b.Bottom - ph;
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, x, y, pw, ph, Native.SWP_NOACTIVATE);
        _winPx = new Native.RECT { Left = x, Top = y, Right = x + pw, Bottom = y + ph };
        _layoutDirty = true;
    }

    // ---------- boucle principale ----------

    private void Tick()
    {
        Native.GetCursorPos(out var pt);
        long now = _clock.ElapsedMilliseconds;
        CloseMenuOnOutsideClick(pt);
        if (now - _lastEval >= 100)
        {
            _lastEval = now;
            EvaluateHide(pt, now);
        }

        double target = _wantHidden ? 1 : 0;
        if (!_wantHidden && !IsVisible)
        {
            _hideProgress = 1;
            Show();
            Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }

        if (Math.Abs(target - _hideProgress) > 0.002) _hideProgress += (target - _hideProgress) * 0.28;
        else _hideProgress = target;

        if (_wantHidden && _hideProgress >= 1 && IsVisible) { Hide(); return; }
        if (!IsVisible) return;

        _slide.Y = _hideProgress * (_h + 4);
        UpdateZoom(pt);
    }

    private void EvaluateHide(Native.POINT pt, long now)
    {
        bool hideCondition = _cfg.HideMode switch
        {
            "always" => true,
            "never" => false,
            _ => ForegroundCovers(),
        };
        if (!hideCondition)
        {
            _wantHidden = false;
            _lastActive = now;
            return;
        }

        bool atEdge = pt.X >= _monPx.Left && pt.X < _monPx.Right && pt.Y >= _monPx.Bottom - 1;
        double hot = (BottomMargin + _pillRestH + 24) * _scale;
        bool inDock = IsVisible && _hideProgress < 0.6 &&
                      pt.X >= _winPx.Left && pt.X <= _winPx.Right && pt.Y >= _winPx.Bottom - hot;
        if (atEdge || inDock || _menuOpen > 0 || _dragging) _lastActive = now;
        _wantHidden = now - _lastActive > 400;
    }

    /// <summary>Vrai si la fenêtre au premier plan est maximisée / plein écran sur l'écran du dock.</summary>
    private bool ForegroundCovers()
    {
        var fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;
        if (fg == _hwnd) return _lastCovers; // menu contextuel ouvert : on garde l'état précédent

        bool covers = false;
        if (Native.IsWindowVisible(fg) && !Native.IsIconic(fg) &&
            Native.MonitorFromWindow(fg, Native.MONITOR_DEFAULTTONEAREST) == _monitor &&
            !IsShellWindow(fg) && !IsCloaked(fg))
        {
            Native.GetWindowRect(fg, out var r);
            covers = Native.IsZoomed(fg) ||
                     (r.Left <= _monPx.Left && r.Top <= _monPx.Top && r.Right >= _monPx.Right && r.Bottom >= _monPx.Bottom);
        }
        return _lastCovers = covers;
    }

    private static bool IsShellWindow(IntPtr hwnd)
    {
        var sb = new StringBuilder(64);
        Native.GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";
    }

    private static bool IsCloaked(IntPtr hwnd) =>
        Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0;

    // ---------- zoom ----------

    private void UpdateZoom(Native.POINT pt)
    {
        int n = _icons.Count;
        if (n == 0) return;

        double size = _cfg.IconSize;
        double mx = (pt.X - _winPx.Left) / _scale, my = (pt.Y - _winPx.Top) / _scale;
        double pillBottom = _h - BottomMargin, pillTop = pillBottom - _pillRestH;
        double sepExtra = SepExtra;
        double restTotal = n * size + (n - 1) * Gap + sepExtra, restLeft = (_w - restTotal) / 2;

        bool active = _hideProgress < 0.5 && my >= pillTop - 24 && my <= _h &&
                      mx >= restLeft - PadX - 40 && mx <= restLeft + restTotal + PadX + 40;

        double sigma = size * 1.15;
        bool settled = true;
        for (int i = 0; i < n; i++)
        {
            var ic = _icons[i];
            double target = 1;
            if (active)
            {
                double d = mx - (restLeft + i * (size + Gap) + size / 2 + (i >= _pinnedCount ? sepExtra : 0));
                target = 1 + (_cfg.Zoom - 1) * Math.Exp(-d * d / (2 * sigma * sigma));
            }
            ic.Scale += (target - ic.Scale) * 0.35;
            if (Math.Abs(target - ic.Scale) < 0.002) ic.Scale = target;
            else settled = false;
        }
        if (!active && settled && !_layoutDirty) return;
        _layoutDirty = false;

        double total = _icons.Sum(ic => size * ic.Scale) + Gap * (n - 1) + sepExtra;
        double x = (_w - total) / 2;
        Canvas.SetLeft(_pill, x - PadX);
        Canvas.SetTop(_pill, pillTop);
        _pill.Width = total + 2 * PadX;
        _pill.Height = _pillRestH;

        int hover = -1;
        double hoverCenter = 0, hoverTop = 0;
        for (int i = 0; i < n; i++)
        {
            var ic = _icons[i];
            double sz = size * ic.Scale;
            double top = pillBottom - PadY - sz;
            if (sepExtra > 0 && i == _pinnedCount)
            {
                double prevEnd = x - Gap;
                x += sepExtra;
                _sep.Height = size * 0.75;
                Canvas.SetLeft(_sep, prevEnd + (x - prevEnd) / 2 - 0.5);
                Canvas.SetTop(_sep, pillBottom - PadY - _sep.Height);
            }
            ic.Image.Width = ic.Image.Height = sz;
            Canvas.SetLeft(ic.Image, x);
            Canvas.SetTop(ic.Image, top);
            Canvas.SetLeft(ic.Dot, x + sz / 2 - _dotSize / 2);
            Canvas.SetTop(ic.Dot, pillBottom - PadY / 2 - _dotSize / 2);
            if (active && mx >= x - Gap / 2 && mx < x + sz + Gap / 2)
            {
                hover = i;
                hoverCenter = x + sz / 2;
                hoverTop = top;
            }
            x += sz + Gap;
        }

        _sep.Visibility = sepExtra > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (hover >= 0)
        {
            _labelText.Text = _icons[hover].Name;
            _label.Visibility = Visibility.Visible;
            _label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double lw = _label.DesiredSize.Width;
            Canvas.SetLeft(_label, Math.Clamp(hoverCenter - lw / 2, 0, Math.Max(0, _w - lw)));
            Canvas.SetTop(_label, hoverTop - 8 - _label.DesiredSize.Height);
        }
        else _label.Visibility = Visibility.Collapsed;
    }

    // ---------- lancement / appli en cours ----------

    private void OnIconClick(DockIcon icon)
    {
        if (icon.Hwnd == IntPtr.Zero) { Launch(icon); return; }

        if (Native.IsIconic(icon.Hwnd))
        {
            Native.ShowWindow(icon.Hwnd, Native.SW_RESTORE);
            Native.SetForegroundWindow(icon.Hwnd);
        }
        else if (IsAppInForeground(icon)) Native.ShowWindow(icon.Hwnd, Native.SW_MINIMIZE); // déjà au premier plan : on réduit
        else Native.SetForegroundWindow(icon.Hwnd);
    }

    /// <summary>La fenêtre active appartient-elle à cette appli ? (le dock ne prend jamais le focus)</summary>
    private static bool IsAppInForeground(DockIcon icon)
    {
        var fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;
        if (fg == icon.Hwnd) return true;
        if (icon.Uwp) return false; // les applis UWP partagent un même processus hôte
        Native.GetWindowThreadProcessId(fg, out uint fgPid);
        Native.GetWindowThreadProcessId(icon.Hwnd, out uint appPid);
        return fgPid != 0 && fgPid == appPid;
    }

    private static void Launch(DockIcon icon)
    {
        try { Process.Start(new ProcessStartInfo(icon.Item.Path) { UseShellExecute = true }); }
        catch (Exception ex) { System.Windows.MessageBox.Show(ex.Message, "DockyDock"); }
    }

    private async Task PollRunning()
    {
        var wins = await Task.Run(ScanWindows);

        // Applis épinglées : point + fenêtre à ramener au premier plan.
        var firstByPath = new Dictionary<string, IntPtr>(StringComparer.OrdinalIgnoreCase);
        foreach (var w in wins)
            if (!w.Uwp) firstByPath.TryAdd(w.Path, w.Hwnd);
        var pinnedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ic in _icons.Where(i => !i.Transient))
        {
            ic.Hwnd = ic.ExePath != null && firstByPath.TryGetValue(ic.ExePath, out var h) ? h : IntPtr.Zero;
            ic.Dot.Visibility = ic.Hwnd != IntPtr.Zero ? Visibility.Visible : Visibility.Collapsed;
            if (ic.ExePath != null) pinnedPaths.Add(ic.ExePath);
        }

        // Applis non épinglées : une entrée par exécutable (une par fenêtre pour les applis UWP).
        var current = new List<Transient>();
        foreach (var w in wins)
        {
            if (!w.Uwp && pinnedPaths.Contains(w.Path)) continue;
            string key = w.Uwp ? "uwp:" + w.Hwnd : w.Path.ToLowerInvariant();
            if (current.Any(t => t.Key == key)) continue;
            current.Add(new Transient { Key = key, Path = w.Path, Hwnd = w.Hwnd, Uwp = w.Uwp, Name = w.Uwp ? w.Title : AppName(w.Path) });
        }

        // On garde l'ordre d'apparition : les anciennes d'abord, les nouvelles à la fin.
        var kept = _transients.Where(t => current.Any(c => c.Key == t.Key)).ToList();
        foreach (var c in current)
        {
            var existing = kept.FirstOrDefault(t => t.Key == c.Key);
            if (existing != null) { existing.Hwnd = c.Hwnd; continue; }
            if (c.Uwp) c.Icon = WindowIcon(c.Hwnd);
            kept.Add(c);
        }

        bool changed = kept.Count != _transients.Count || kept.Where((t, i) => _transients[i] != t).Any();
        foreach (var ic in _icons.Where(i => i.Transient))
            ic.Hwnd = kept.FirstOrDefault(t => t.Path == ic.Item.Path && t.Name == ic.Name)?.Hwnd ?? ic.Hwnd;
        if (!changed || _dragging || _menuOpen > 0) return;

        _transients.Clear();
        _transients.AddRange(kept);
        Rebuild();
    }

    private string AppName(string path)
    {
        if (_appNames.TryGetValue(path, out var cached)) return cached;
        string name;
        try
        {
            var vi = FileVersionInfo.GetVersionInfo(path);
            name = !string.IsNullOrWhiteSpace(vi.FileDescription) ? vi.FileDescription! : System.IO.Path.GetFileNameWithoutExtension(path);
        }
        catch { name = System.IO.Path.GetFileNameWithoutExtension(path); }
        return _appNames[path] = name;
    }

    private static ImageSource? WindowIcon(IntPtr hwnd)
    {
        try
        {
            Native.SendMessageTimeout(hwnd, 0x7F, new IntPtr(1), IntPtr.Zero, 2, 100, out var res);
            IntPtr hicon = res != IntPtr.Zero ? res : Native.GetClassLongPtr(hwnd, -14);
            if (hicon == IntPtr.Zero) return null;
            var src = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(hicon, Int32Rect.Empty, System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        catch { return null; }
    }

    /// <summary>Fenêtres "de barre des tâches" (comme Alt+Tab), dans l'ordre Z.</summary>
    private static List<WinInfo> ScanWindows()
    {
        var list = new List<WinInfo>();
        uint self = (uint)Environment.ProcessId;
        Native.EnumWindows((hwnd, _) =>
        {
            try
            {
                if (!Native.IsWindowVisible(hwnd) || IsCloaked(hwnd) || IsShellWindow(hwnd)) return true;
                int ex = Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE);
                bool appWindow = (ex & Native.WS_EX_APPWINDOW) != 0;
                if ((ex & Native.WS_EX_TOOLWINDOW) != 0 && !appWindow) return true;
                if (Native.GetWindow(hwnd, 4) != IntPtr.Zero && !appWindow) return true; // fenêtre possédée (dialogue...)
                int len = Native.GetWindowTextLength(hwnd);
                if (len == 0) return true;
                Native.GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid == self) return true;

                IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, (int)pid);
                if (h == IntPtr.Zero) return true;
                string path;
                try
                {
                    var sb = new StringBuilder(1024);
                    int size = sb.Capacity;
                    if (!Native.QueryFullProcessImageNameW(h, 0, sb, ref size)) return true;
                    path = sb.ToString();
                }
                finally { Native.CloseHandle(h); }

                var title = new StringBuilder(len + 1);
                Native.GetWindowText(hwnd, title, title.Capacity);
                bool uwp = path.EndsWith("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase);
                list.Add(new WinInfo(path, hwnd, title.ToString(), uwp));
            }
            catch { /* fenêtre disparue pendant l'énumération */ }
            return true;
        }, IntPtr.Zero);
        return list;
    }

    private static string? ResolveExe(string path)
    {
        string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".exe") return path;
        if (ext != ".lnk") return null;
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return null;
            dynamic shell = Activator.CreateInstance(shellType)!;
            string target = shell.CreateShortcut(path).TargetPath;
            return string.IsNullOrEmpty(target) ? null : target;
        }
        catch { return null; }
    }

    // ---------- glisser-déposer ----------

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DragFormat) || e.Data.GetDataPresent(TransientFormat)) e.Effects = DragDropEffects.Move;
        else if (e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(ShellIdListFormat))
        {
            // Le menu Démarrer n'autorise souvent que "Lien" : on prend le premier effet permis par la source.
            var allowed = e.AllowedEffects;
            e.Effects = (allowed & DragDropEffects.Link) != 0 ? DragDropEffects.Link
                      : (allowed & DragDropEffects.Copy) != 0 ? DragDropEffects.Copy
                      : allowed & DragDropEffects.Move;
        }
        else e.Effects = DragDropEffects.None;
        LogFormats(e.Data);
        e.Handled = true;
    }

    private string _lastLoggedFormats = "";

    private void LogFormats(IDataObject data)
    {
        try
        {
            string formats = string.Join(", ", data.GetFormats());
            if (formats == _lastLoggedFormats) return;
            _lastLoggedFormats = formats;
            File.AppendAllText(System.IO.Path.Combine(DockConfig.Dir, "drag.log"), $"{DateTime.Now:T} formats: {formats}{Environment.NewLine}");
        }
        catch { /* le log est purement diagnostique */ }
    }

    /// <summary>Lit le format "Shell IDList Array" (glisser depuis le menu Démarrer, etc.).</summary>
    private static List<(string Path, string? Name)> ReadShellItems(IDataObject data)
    {
        var result = new List<(string, string?)>();
        if (!data.GetDataPresent(ShellIdListFormat) || data.GetData(ShellIdListFormat) is not MemoryStream ms) return result;

        byte[] buf = ms.ToArray();
        var pin = System.Runtime.InteropServices.GCHandle.Alloc(buf, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            IntPtr basePtr = pin.AddrOfPinnedObject();
            uint count = BitConverter.ToUInt32(buf, 0);
            IntPtr parent = basePtr + (int)BitConverter.ToUInt32(buf, 4);
            for (int i = 0; i < count; i++)
            {
                IntPtr child = basePtr + (int)BitConverter.ToUInt32(buf, 8 + 4 * i);
                IntPtr full = Native.ILCombine(parent, child);
                if (full == IntPtr.Zero) continue;
                try
                {
                    string? path = ShellName(full, Native.SIGDN_DESKTOPABSOLUTEPARSING);
                    string? name = ShellName(full, Native.SIGDN_NORMALDISPLAY);
                    if (!string.IsNullOrEmpty(path)) result.Add((path, name));
                }
                finally { Native.ILFree(full); }
            }
        }
        finally { pin.Free(); }
        return result;
    }

    private static string? ShellName(IntPtr pidl, uint sigdn)
    {
        if (Native.SHGetNameFromIDList(pidl, sigdn, out var p) != 0 || p == IntPtr.Zero) return null;
        try { return System.Runtime.InteropServices.Marshal.PtrToStringUni(p); }
        finally { Native.CoTaskMemFree(p); }
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        int index = DropIndex(e.GetPosition(_canvas).X);
        var items = _cfg.Items;

        if (e.Data.GetData(DragFormat) is int from)
        {
            if (from < 0 || from >= items.Count) return;
            var moved = items[from];
            items.RemoveAt(from);
            if (index > from) index--;
            items.Insert(Math.Clamp(index, 0, items.Count), moved);
        }
        else if (e.Data.GetData(TransientFormat) is string openPath)
        {
            if (!items.Any(i => string.Equals(i.Path, openPath, StringComparison.OrdinalIgnoreCase)))
                items.Insert(Math.Clamp(index, 0, items.Count), new DockItem { Path = openPath });
            _transients.RemoveAll(t => t.Path.Equals(openPath, StringComparison.OrdinalIgnoreCase));
        }
        else if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            foreach (var f in files)
            {
                if (items.Any(i => string.Equals(i.Path, f, StringComparison.OrdinalIgnoreCase))) continue;
                items.Insert(Math.Clamp(index++, 0, items.Count), new DockItem { Path = f });
            }
        }
        else
        {
            var shellItems = ReadShellItems(e.Data);
            try { File.AppendAllText(System.IO.Path.Combine(DockConfig.Dir, "drag.log"), $"  items: {string.Join(" | ", shellItems.Select(s => s.Path + " => " + s.Name))}{Environment.NewLine}"); } catch { }
            if (shellItems.Count == 0) return;
            foreach (var (path, name) in shellItems)
            {
                if (items.Any(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase))) continue;
                items.Insert(Math.Clamp(index++, 0, items.Count), new DockItem { Path = path, Name = name });
            }
        }

        _cfg.Save();
        Rebuild();
        e.Handled = true;
    }

    private int DropIndex(double x)
    {
        for (int i = 0; i < _pinnedCount; i++)
        {
            var img = _icons[i].Image;
            if (x < Canvas.GetLeft(img) + img.Width / 2) return i;
        }
        return _pinnedCount;
    }

    /// <summary>Largeur ajoutée entre les applis épinglées et les autres (séparateur).</summary>
    private double SepExtra => _pinnedCount > 0 && _icons.Count > _pinnedCount ? Gap + 2 : 0;
}
