using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace DockyDock;

/// <summary>Fenêtre de recherche façon Spotlight : applis, fichiers, calculs, web.</summary>
public sealed class SearchWindow : Window
{
    private const double PanelWidth = 640, Margin_ = 16, RowHeight = 56;
    private const int MaxRows = 8;

    private readonly DockConfig _cfg;
    private readonly AppIndex _apps;
    private readonly Border _panel = new();
    private readonly TextBox _input = new();
    private readonly System.Windows.Shapes.Path _glass = new();
    private readonly StackPanel _rows = new();
    private readonly ScrollViewer _scroll = new();
    private readonly Border _separator = new() { Height = 1, Visibility = Visibility.Collapsed };

    private readonly List<SearchResult> _results = new();
    private int _selected;
    private CancellationTokenSource? _cts;
    private long _shownAt;
    private IntPtr _hwnd;
    private readonly System.Windows.Threading.DispatcherTimer _focusWatch = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private Theme _theme = new();
    private Brush _textBrush = Brushes.White, _subBrush = Brushes.Gray, _highlight = Brushes.Transparent;

    internal SearchWindow(DockConfig cfg, AppIndex apps)
    {
        _cfg = cfg;
        _apps = apps;

        Title = "DockyDock - Recherche";
        Icon = AppIcon.Source;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        SizeToContent = SizeToContent.Height;
        Width = PanelWidth + 2 * Margin_;
        WindowStartupLocation = WindowStartupLocation.Manual;

        _input.BorderThickness = new Thickness(0);
        _input.Background = Brushes.Transparent;
        _input.FontSize = 22;
        _input.Padding = new Thickness(4, 8, 4, 8);
        _input.VerticalContentAlignment = VerticalAlignment.Center;

        _scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _scroll.MaxHeight = MaxRows * RowHeight;
        _scroll.Content = _rows;
        _scroll.Visibility = Visibility.Collapsed;

        var header = new Grid { Margin = new Thickness(10, 4, 10, 4) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition());
        // Loupe dessinée (et non l'emoji, qui est coloré) : elle prend la couleur du texte du thème.
        _glass.Data = Geometry.Parse("M9,2.5 A6.5,6.5 0 1 0 9,15.5 A6.5,6.5 0 1 0 9,2.5 M13.8,13.8 L19.5,19.5");
        _glass.StrokeThickness = 2.2;
        _glass.StrokeStartLineCap = PenLineCap.Round;
        _glass.StrokeEndLineCap = PenLineCap.Round;
        _glass.Width = 22;
        _glass.Height = 22;
        _glass.Stretch = Stretch.None;
        _glass.VerticalAlignment = VerticalAlignment.Center;
        _glass.Margin = new Thickness(8, 0, 10, 0);
        var glass = _glass;
        Grid.SetColumn(_input, 1);
        header.Children.Add(glass);
        header.Children.Add(_input);

        var stack = new StackPanel();
        stack.Children.Add(header);
        stack.Children.Add(_separator);
        stack.Children.Add(_scroll);
        _panel.Child = stack;
        _panel.Margin = new Thickness(Margin_);
        _panel.Padding = new Thickness(6);
        Content = _panel;

        _input.TextChanged += (_, _) => OnQueryChanged();
        PreviewKeyDown += OnKeyDown;
        Deactivated += (_, _) =>
        {
            if (Environment.TickCount64 - _shownAt > 250) HideSearch();
        };
        // Filet de sécurité : si Windows n'a jamais donné le focus à la fenêtre, Deactivated ne se déclenche pas
        // alors que le curseur clignote. On vérifie donc régulièrement qu'elle est bien la fenêtre active.
        _focusWatch.Tick += (_, _) =>
        {
            if (!IsVisible) { _focusWatch.Stop(); return; }
            if (Environment.TickCount64 - _shownAt < 500) return;
            if (Native.GetForegroundWindow() != _hwnd) HideSearch();
        };
    }

    // ---------- affichage ----------

    public void ShowSearch()
    {
        ApplyTheme();
        _input.Text = "";
        ClearResults();
        _apps.RefreshAsync();

        _hwnd = new WindowInteropHelper(this).EnsureHandle();
        int ex = Native.GetWindowLong(_hwnd, Native.GWL_EXSTYLE);
        Native.SetWindowLong(_hwnd, Native.GWL_EXSTYLE, ex | Native.WS_EX_TOOLWINDOW);

        _shownAt = Environment.TickCount64;
        Place();
        Show();
        Place();
        ForceForeground();
        Activate();
        _input.Focus();
        Keyboard.Focus(_input);
        _focusWatch.Start();
    }

    public void HideSearch()
    {
        _cts?.Cancel();
        _focusWatch.Stop();
        if (IsVisible) Hide();
    }

    /// <summary>Centre en haut de l'écran où se trouve la souris.</summary>
    private void Place()
    {
        Native.GetCursorPos(out var pt);
        var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(pt.X, pt.Y));
        var b = screen.Bounds;
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, b.Left + 10, b.Top + 10, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        double scale = Native.GetDpiForWindow(_hwnd) / 96.0;
        int pw = (int)Math.Round(Width * scale);
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, b.Left + (b.Width - pw) / 2, b.Top + (int)(b.Height * 0.16), 0, 0,
            Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    /// <summary>Windows refuse souvent de donner le focus à une fenêtre qui n'est pas au premier plan : on s'attache au thread actif.</summary>
    private void ForceForeground()
    {
        var fg = Native.GetForegroundWindow();
        uint fgThread = fg == IntPtr.Zero ? 0 : Native.GetWindowThreadProcessId(fg, out _);
        uint me = Native.GetCurrentThreadId();
        bool attached = fgThread != 0 && fgThread != me && Native.AttachThreadInput(me, fgThread, true);
        Native.SetForegroundWindow(_hwnd);
        Native.BringWindowToTop(_hwnd);
        if (attached) Native.AttachThreadInput(me, fgThread, false);
    }

    private void ApplyTheme()
    {
        var t = _theme = ThemeManager.Get(_cfg.Theme);
        var bg = Theme.ParseColor(t.Background, Color.FromRgb(0x30, 0x30, 0x36));
        double alpha = Math.Max(0.9, _cfg.Opacity ?? t.Opacity);
        _panel.Background = new SolidColorBrush(Color.FromArgb((byte)(alpha * 255), bg.R, bg.G, bg.B));
        _panel.BorderBrush = new SolidColorBrush(Theme.ParseColor(t.InnerBorderColor ?? t.BorderColor, Color.FromArgb(0x30, 255, 255, 255)));
        _panel.BorderThickness = new Thickness(1);
        _panel.CornerRadius = new CornerRadius(Math.Max(14, t.CornerRadius * 1.6));
        _panel.Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 4, Direction = 270, Opacity = 0.45, Color = Colors.Black };

        var fg = Theme.ParseColor(t.LabelForeground, Colors.White);
        _textBrush = new SolidColorBrush(fg);
        _subBrush = new SolidColorBrush(Color.FromArgb(0x99, fg.R, fg.G, fg.B));
        _highlight = new SolidColorBrush(Color.FromArgb(0x3A, 0xFF, 0xFF, 0xFF));
        _separator.Background = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
        _glass.Stroke = _textBrush;
        _input.Foreground = _textBrush;
        _input.CaretBrush = _textBrush;
        var font = t.FontFamily != null ? new FontFamily(t.FontFamily) : SystemFonts.MessageFontFamily;
        FontFamily = font;
    }

    // ---------- recherche ----------

    private void OnQueryChanged()
    {
        _cts?.Cancel();
        string text = _input.Text.Trim();
        if (text.Length == 0) { ClearResults(); return; }

        var cts = _cts = new CancellationTokenSource();
        var instant = BuildResults(text, null);
        SetResults(instant, keepSelection: false);

        _ = SearchFilesAsync(text, cts.Token);
    }

    private async Task SearchFilesAsync(string text, CancellationToken token)
    {
        try
        {
            await Task.Delay(180, token);
            var files = await Task.Run(() => FileSearch.Query(text, 6), token);
            if (token.IsCancellationRequested) return;
            SetResults(BuildResults(text, files), keepSelection: true);
        }
        catch (OperationCanceledException) { }
    }

    private List<SearchResult> BuildResults(string text, List<SearchResult>? files)
    {
        var list = new List<SearchResult>();

        string? calc = Calculator.TryEvaluate(text);
        if (calc != null)
            list.Add(new SearchResult { Kind = ResultKind.Calc, Title = calc, Subtitle = $"{text} - Entrée pour copier le résultat", Copy = calc });

        list.AddRange(_apps.Query(text, 6));
        if (files != null) list.AddRange(files);

        if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            list.Add(new SearchResult { Kind = ResultKind.Url, Title = "Ouvrir " + text, Subtitle = "Navigateur", Target = text });

        list.Add(new SearchResult
        {
            Kind = ResultKind.Web,
            Title = $"Rechercher « {text} » sur le web",
            Subtitle = "Navigateur par défaut",
            Target = string.Format(_cfg.WebSearchUrl, Uri.EscapeDataString(text)),
        });
        return list;
    }

    private void ClearResults()
    {
        _results.Clear();
        _rows.Children.Clear();
        _scroll.Visibility = Visibility.Collapsed;
        _separator.Visibility = Visibility.Collapsed;
    }

    private void SetResults(List<SearchResult> results, bool keepSelection)
    {
        int previous = keepSelection ? _selected : 0;
        _results.Clear();
        _results.AddRange(results);
        _selected = _results.Count == 0 ? 0 : Math.Clamp(previous, 0, _results.Count - 1);
        Render();
    }

    private void Render()
    {
        _rows.Children.Clear();
        for (int i = 0; i < _results.Count; i++)
            _rows.Children.Add(BuildRow(_results[i], i));
        bool any = _results.Count > 0;
        _scroll.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        _separator.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        UpdateSelection();
    }

    private UIElement BuildRow(SearchResult r, int index)
    {
        var grid = new Grid { Height = RowHeight - 6, Margin = new Thickness(2, 3, 2, 3) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        UIElement icon;
        ImageSource? source = r.IconPath != null ? IconLoader.Load(r.IconPath) : null;
        if (source != null)
        {
            var img = new Image { Source = source, Width = 32, Height = 32, HorizontalAlignment = HorizontalAlignment.Center };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            icon = img;
        }
        else
        {
            string glyph = r.Kind switch { ResultKind.Calc => "🧮", ResultKind.Web => "🌐", ResultKind.Url => "🔗", ResultKind.File => "📄", _ => "▫" };
            icon = new TextBlock { Text = glyph, FontSize = 24, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        }
        grid.Children.Add(icon);

        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(new TextBlock
        {
            Text = r.Title, FontSize = r.Kind == ResultKind.Calc ? 20 : 15, Foreground = _textBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        texts.Children.Add(new TextBlock
        {
            Text = r.Subtitle, FontSize = 11, Foreground = _subBrush, TextTrimming = TextTrimming.CharacterEllipsis,
        });
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);

        var row = new Border { Child = grid, CornerRadius = new CornerRadius(9), Padding = new Thickness(4, 0, 10, 0), Tag = index, Background = Brushes.Transparent };
        row.MouseEnter += (_, _) => { _selected = index; UpdateSelection(); };
        row.MouseLeftButtonUp += (_, _) => { _selected = index; Activate(r); };
        return row;
    }

    private void UpdateSelection()
    {
        for (int i = 0; i < _rows.Children.Count; i++)
            if (_rows.Children[i] is Border b)
                b.Background = i == _selected ? _highlight : Brushes.Transparent;
        if (_selected >= 0 && _selected < _rows.Children.Count)
            ((FrameworkElement)_rows.Children[_selected]).BringIntoView();
    }

    // ---------- clavier / lancement ----------

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                HideSearch();
                e.Handled = true;
                break;
            case Key.Down:
            case Key.Tab when Keyboard.Modifiers == ModifierKeys.None:
                if (_results.Count > 0) { _selected = (_selected + 1) % _results.Count; UpdateSelection(); }
                e.Handled = true;
                break;
            case Key.Up:
                if (_results.Count > 0) { _selected = (_selected - 1 + _results.Count) % _results.Count; UpdateSelection(); }
                e.Handled = true;
                break;
            case Key.Enter:
                if (_selected < _results.Count)
                {
                    var r = _results[_selected];
                    if (Keyboard.Modifiers == ModifierKeys.Shift && r.Kind == ResultKind.File)
                        RevealInExplorer(r);
                    else
                        Activate(r);
                }
                e.Handled = true;
                break;
        }
    }

    private static void RevealInExplorer(SearchResult r)
    {
        try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{r.Target}\""); } catch { }
    }

    private void Activate(SearchResult r)
    {
        HideSearch();
        try
        {
            switch (r.Kind)
            {
                case ResultKind.Calc:
                    if (r.Copy != null) Clipboard.SetText(r.Copy);
                    break;
                case ResultKind.App:
                    SearchUsage.Increment(r.Target);
                    if (r.Target.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{r.Target}\"") { UseShellExecute = true });
                    else
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(r.Target) { UseShellExecute = true });
                    break;
                default:
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(r.Target) { UseShellExecute = true });
                    break;
            }
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(ex.Message, "DockyDock");
        }
    }
}
