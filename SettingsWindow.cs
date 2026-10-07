using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace DockyDock;

public sealed class SettingsWindow : Window
{
    private readonly DockConfig _cfg;
    private readonly DockWindow _dock;
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private bool _loading = true;

    public SettingsWindow(DockConfig cfg, DockWindow dock)
    {
        _cfg = cfg;
        _dock = dock;

        Title = "Paramètres - DockyDock";
        Width = 400;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;

        var root = new StackPanel { Margin = new Thickness(18) };
        Content = root;

        var size = AddSlider(root, "Taille des icônes", 28, 128, 1, _cfg.IconSize, v => $"{v:0} px", v => _cfg.IconSize = Math.Round(v));
        var offset = AddSlider(root, "Position verticale (0 = bord de l'écran)", 0, 150, 1, _cfg.VerticalOffset, v => $"{v:0} px", v => _cfg.VerticalOffset = Math.Round(v));
        var zoom = AddSlider(root, "Zoom au survol (100 % = aucun zoom)", 1.0, 3.0, 0.05, _cfg.Zoom, v => $"{v * 100:0} %", v => _cfg.Zoom = Math.Round(v, 2));

        var themes = ThemeManager.LoadAll().Select(t => new KeyValuePair<string, object>(t.Name, t.Name)).ToList();
        Slider? opacity = null;
        AddCombo(root, "Thème", themes, _cfg.Theme, v =>
        {
            _cfg.Theme = (string)v;
            _cfg.Opacity = null; // on repart de l'opacité du nouveau thème
            if (opacity != null)
            {
                bool wasLoading = _loading;
                _loading = true;
                opacity.Value = ThemeManager.Get(_cfg.Theme).Opacity;
                _loading = wasLoading;
            }
        });
        opacity = AddSlider(root, "Opacité du dock (0 = invisible, 1 = plein)", 0, 1, 0.01,
            _cfg.Opacity ?? ThemeManager.Get(_cfg.Theme).Opacity, v => $"{v:0.00}", v => _cfg.Opacity = Math.Round(v, 2));

        var screens = new List<KeyValuePair<string, object>> { new("Écran principal", -1) };
        var all = System.Windows.Forms.Screen.AllScreens;
        for (int i = 0; i < all.Length; i++)
            screens.Add(new($"Écran {i + 1} ({all[i].Bounds.Width}×{all[i].Bounds.Height}){(all[i].Primary ? " - principal" : "")}", i));
        AddCombo(root, "Écran", screens, _cfg.Monitor, v => _cfg.Monitor = (int)v);

        var hide = new List<KeyValuePair<string, object>>
        {
            new("Masqué sous une fenêtre maximisée", "maximized"),
            new("Toujours masqué", "always"),
            new("Toujours visible", "never"),
        };
        AddCombo(root, "Masquage", hide, _cfg.HideMode, v => _cfg.HideMode = (string)v);

        var startup = new CheckBox { Content = "Lancer avec Windows", IsChecked = AutoStart.IsEnabled, Margin = new Thickness(0, 14, 0, 0) };
        startup.Click += (_, _) => AutoStart.Set(startup.IsChecked == true);
        root.Children.Add(startup);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        var folder = new Button { Content = "Dossier des thèmes", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 8, 0) };
        folder.Click += (_, _) =>
        {
            ThemeManager.EnsureBuiltIns();
            Process.Start(new ProcessStartInfo(ThemeManager.Dir) { UseShellExecute = true });
        };
        var close = new Button { Content = "Fermer", Padding = new Thickness(18, 4, 18, 4), IsDefault = true };
        close.Click += (_, _) => Close();
        buttons.Children.Add(folder);
        buttons.Children.Add(close);
        root.Children.Add(buttons);

        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); _cfg.Save(); };
        Closed += (_, _) => { _saveTimer.Stop(); _cfg.Save(); };
        _loading = false;
        _ = size; _ = zoom; _ = offset;
    }

    private void Changed()
    {
        if (_loading) return;
        _dock.Rebuild();
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private Slider AddSlider(Panel parent, string label, double min, double max, double step, double value,
                             Func<double, string> format, Action<double> apply)
    {
        var header = new DockPanel { Margin = new Thickness(0, 10, 0, 2) };
        var valueText = new TextBlock { Text = format(value), FontWeight = FontWeights.SemiBold };
        DockPanel.SetDock(valueText, Dock.Right);
        header.Children.Add(valueText);
        header.Children.Add(new TextBlock { Text = label });

        var slider = new Slider { Minimum = min, Maximum = max, SmallChange = step, LargeChange = step * 5, TickFrequency = step, Value = value };
        slider.ValueChanged += (_, e) =>
        {
            if (_loading) return;
            apply(e.NewValue);
            valueText.Text = format(e.NewValue);
            Changed();
        };
        parent.Children.Add(header);
        parent.Children.Add(slider);
        return slider;
    }

    private void AddCombo(Panel parent, string label, List<KeyValuePair<string, object>> items, object selected, Action<object> apply)
    {
        parent.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 12, 0, 2) });
        var combo = new ComboBox { ItemsSource = items, DisplayMemberPath = "Key", SelectedValuePath = "Value" };
        combo.SelectedValue = selected;
        if (combo.SelectedIndex < 0) combo.SelectedIndex = 0;
        combo.SelectionChanged += (_, _) =>
        {
            if (_loading || combo.SelectedValue == null) return;
            apply(combo.SelectedValue);
            Changed();
        };
        parent.Children.Add(combo);
    }
}
