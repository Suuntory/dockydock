using System;
using System.Windows.Threading;

namespace DockyDock;

/// <summary>Recherche globale : raccourci clavier + fenêtre de recherche. Un seul exemplaire, créé dans Program.</summary>
public sealed class SearchFeature : IDisposable
{
    public static SearchFeature? Instance { get; private set; }

    private readonly DockConfig _cfg;
    private readonly AppIndex _apps = new();
    private readonly HotkeyService _hotkey;
    private SearchWindow? _window;

    public SearchFeature(DockConfig cfg, Dispatcher dispatcher)
    {
        _cfg = cfg;
        _hotkey = new HotkeyService(dispatcher, Toggle);
        Instance = this;
    }

    /// <summary>Applique la config : (dés)active le raccourci et précharge l'index des applis.</summary>
    public void Apply()
    {
        if (_cfg.SearchEnabled)
        {
            _hotkey.SetSpec(HotkeySpec.Parse(_cfg.SearchHotkey) ?? HotkeySpec.Parse("Win+Alt"));
            _apps.RefreshAsync();
        }
        else
        {
            _hotkey.SetSpec(null);
            _window?.HideSearch();
        }
    }

    public void Toggle()
    {
        if (!_cfg.SearchEnabled) return;
        _window ??= new SearchWindow(_cfg, _apps);
        if (_window.IsVisible) _window.HideSearch();
        else _window.ShowSearch();
    }

    /// <summary>Enregistre le prochain raccourci tapé. done(raccourci, erreur) ; Échap annule (les deux null).</summary>
    public void BeginCapture(Action<string?, string?> done) => _hotkey.BeginCapture(done);

    public void CancelCapture() => _hotkey.CancelCapture();

    public void Dispose() => _hotkey.Dispose();
}
