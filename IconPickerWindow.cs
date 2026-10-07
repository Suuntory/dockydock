using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DockyDock;

/// <summary>
/// Choix d'une icône parmi celles du dossier "icons". Un clic sur une icône l'applique tout de suite dans le dock
/// (<paramref name="onPreview"/>), mais rien n'est enregistré tant que "OK" n'est pas cliqué (<paramref name="onCommit"/>).
/// Annuler ou fermer la fenêtre remet l'icône d'origine.
/// </summary>
public sealed class IconPickerWindow : Window
{
    private static readonly Brush SelectedBrush = new SolidColorBrush(Color.FromRgb(0x2D, 0x7D, 0xD2));

    private readonly Action<string?> _onPreview;
    private readonly string? _original;
    private readonly Dictionary<Button, string> _buttons = new();
    private string? _selected;
    private bool _committed;

    public IconPickerWindow(string itemName, string? currentIcon, Action<string?> onPreview, Action<string?> onCommit)
    {
        _onPreview = onPreview;
        _original = currentIcon;
        _selected = currentIcon;

        Title = "Changer l'icône - " + itemName;
        Width = 560;
        Height = 560;
        MinWidth = 320;
        MinHeight = 260;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var root = new Grid { Margin = new Thickness(14) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Content = root;

        var header = new TextBlock
        {
            Text = "Dossier : " + IconLibrary.Dir,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.Gray,
            FontSize = 11,
            Margin = new Thickness(0, 0, 0, 8),
        };
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        var search = new TextBox { Margin = new Thickness(0, 0, 0, 8), Padding = new Thickness(4), ToolTip = "Filtrer par nom" };
        Grid.SetRow(search, 1);
        root.Children.Add(search);

        var wrap = new WrapPanel();
        var files = IconLibrary.List();
        if (files.Count == 0)
            wrap.Children.Add(new TextBlock
            {
                Text = "Aucune icône pour le moment.\nAjoute des images (.png, .ico, .jpg, .bmp) dans le dossier, puis rouvre cette fenêtre.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(4),
            });
        foreach (var file in files)
        {
            string fileName = Path.GetFileName(file);
            var panel = new StackPanel();
            panel.Children.Add(new Image { Source = IconLibrary.LoadThumb(file, 112), Width = 56, Height = 56, Stretch = Stretch.Uniform });
            panel.Children.Add(new TextBlock
            {
                Text = Path.GetFileNameWithoutExtension(file),
                FontSize = 11,
                TextTrimming = TextTrimming.CharacterEllipsis,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 4, 0, 0),
            });
            var button = new Button
            {
                Content = panel, Width = 92, Height = 100, Margin = new Thickness(3), ToolTip = fileName,
                BorderThickness = new Thickness(2),
            };
            button.Click += (_, _) => Select(fileName);
            _buttons[button] = fileName;
            wrap.Children.Add(button);
        }
        search.TextChanged += (_, _) =>
        {
            string q = search.Text.Trim();
            foreach (var b in _buttons.Keys)
                b.Visibility = q.Length == 0 || ((string)b.ToolTip).Contains(q, StringComparison.OrdinalIgnoreCase)
                    ? Visibility.Visible : Visibility.Collapsed;
        };
        var scroll = new ScrollViewer { Content = wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(scroll, 2);
        root.Children.Add(scroll);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        var reset = new Button { Content = "Icône par défaut", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 8, 0) };
        reset.Click += (_, _) => Select(null);
        var open = new Button { Content = "Ouvrir le dossier", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 16, 0) };
        open.Click += (_, _) => Process.Start(new ProcessStartInfo(IconLibrary.Dir) { UseShellExecute = true });
        var ok = new Button { Content = "OK", Padding = new Thickness(22, 4, 22, 4), Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        ok.Click += (_, _) => { _committed = true; onCommit(_selected); Close(); };
        var cancel = new Button { Content = "Annuler", Padding = new Thickness(14, 4, 14, 4), IsCancel = true };
        buttons.Children.Add(reset);
        buttons.Children.Add(open);
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        Grid.SetRow(buttons, 3);
        root.Children.Add(buttons);

        Closed += (_, _) => { if (!_committed) _onPreview(_original); };
        Highlight();
    }

    private void Select(string? fileName)
    {
        _selected = fileName;
        Highlight();
        _onPreview(fileName);
    }

    private void Highlight()
    {
        foreach (var (button, name) in _buttons)
            button.BorderBrush = name == _selected ? SelectedBrush : null;
    }
}
