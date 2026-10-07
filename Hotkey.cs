using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Threading;

namespace DockyDock;

/// <summary>Raccourci : des modificateurs (Win, Ctrl, Alt, Shift) et éventuellement une touche. Sans touche = combinaison de modificateurs seuls.</summary>
internal sealed class HotkeySpec
{
    public bool Win, Ctrl, Alt, Shift;
    /// <summary>Code de touche virtuelle, 0 pour une combinaison de modificateurs seuls.</summary>
    public int Vk;

    public static HotkeySpec? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var spec = new HotkeySpec();
        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "win": case "windows": case "super": spec.Win = true; break;
                case "ctrl": case "control": spec.Ctrl = true; break;
                case "alt": spec.Alt = true; break;
                case "shift": spec.Shift = true; break;
                default:
                    if (spec.Vk != 0 || !Enum.TryParse<Key>(raw, true, out var key) || key == Key.None) return null;
                    spec.Vk = KeyInterop.VirtualKeyFromKey(key);
                    break;
            }
        }
        int mods = spec.ModifierCount;
        if (mods == 0) return null;                  // une touche seule volerait cette touche partout
        if (spec.Vk == 0 && mods < 2) return null;   // un modificateur seul se déclencherait à chaque appui
        return spec;
    }

    public int ModifierCount => (Win ? 1 : 0) + (Ctrl ? 1 : 0) + (Alt ? 1 : 0) + (Shift ? 1 : 0);

    public override string ToString()
    {
        var parts = new List<string>();
        if (Win) parts.Add("Win");
        if (Ctrl) parts.Add("Ctrl");
        if (Alt) parts.Add("Alt");
        if (Shift) parts.Add("Shift");
        if (Vk != 0) parts.Add(KeyInterop.KeyFromVirtualKey(Vk).ToString());
        return string.Join("+", parts);
    }
}

/// <summary>
/// Détecte le raccourci avec un hook clavier global (RegisterHotKey ne sait pas gérer "Win+Alt" sans touche).
/// Les autres combinaisons Windows (Win+R, Win+E...) ne sont pas touchées : seul le raccourci choisi est détecté.
/// </summary>
internal sealed class HotkeyService : IDisposable
{
    private const int WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, WM_SYSKEYDOWN = 0x104, WM_SYSKEYUP = 0x105;
    private const int VK_MASK = 0xE8; // touche non attribuée : sert à "occuper" Win/Alt pour qu'ils n'ouvrent ni Démarrer ni un menu

    /// <summary>Réservé aux tests automatisés : DOCKYDOCK_ACCEPT_INJECTED=1 fait accepter les touches simulées.</summary>
    private static readonly bool AcceptInjected = Environment.GetEnvironmentVariable("DOCKYDOCK_ACCEPT_INJECTED") == "1";

    private readonly Dispatcher _dispatcher;
    private readonly Action _onTrigger;
    private readonly Native.LowLevelKeyboardProc _proc; // gardé en champ : le GC ne doit pas le collecter
    private IntPtr _hook;
    private HotkeySpec? _spec;

    private bool _win, _ctrl, _alt, _shift;
    private bool _other, _armed;
    private readonly HashSet<int> _swallowUp = new();

    private Action<string?, string?>? _capture;
    private readonly HashSet<int> _capHeld = new();
    private bool _capWin, _capCtrl, _capAlt, _capShift;
    private int _capKey;
    private DispatcherTimer? _capTimer;

    public HotkeyService(Dispatcher dispatcher, Action onTrigger)
    {
        _dispatcher = dispatcher;
        _onTrigger = onTrigger;
        _proc = HookProc;
    }

    public void SetSpec(HotkeySpec? spec)
    {
        _spec = spec;
        _win = _ctrl = _alt = _shift = _other = _armed = false;
        _swallowUp.Clear();
        UpdateHook();
    }

    /// <summary>Attend une combinaison (rien n'est transmis aux autres applis). Rappelle avec (raccourci, erreur).</summary>
    public void BeginCapture(Action<string?, string?> done)
    {
        CancelCapture();
        _capture = done;
        _capHeld.Clear();
        _capWin = _capCtrl = _capAlt = _capShift = false;
        _capKey = 0;
        _capTimer = new DispatcherTimer(DispatcherPriority.Normal, _dispatcher) { Interval = TimeSpan.FromSeconds(15) };
        _capTimer.Tick += (_, _) => FinishCapture(null, "Délai dépassé.");
        _capTimer.Start();
        UpdateHook();
    }

    public void CancelCapture()
    {
        if (_capture == null) return;
        FinishCapture(null, null);
    }

    private void FinishCapture(string? spec, string? error)
    {
        var done = _capture;
        _capture = null;
        _capTimer?.Stop();
        _capTimer = null;
        UpdateHook();
        done?.Invoke(spec, error);
    }

    private void UpdateHook()
    {
        bool need = _spec != null || _capture != null;
        if (need && _hook == IntPtr.Zero)
        {
            string module = Process.GetCurrentProcess().MainModule?.ModuleName ?? "";
            _hook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _proc, Native.GetModuleHandle(module), 0);
        }
        else if (!need && _hook != IntPtr.Zero)
        {
            Native.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            try
            {
                var k = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
                if ((k.flags & Native.LLKHF_INJECTED) == 0 || AcceptInjected)
                {
                    int msg = wParam.ToInt32();
                    bool down = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
                    bool up = msg == WM_KEYUP || msg == WM_SYSKEYUP;
                    if ((down || up) && Handle((int)k.vkCode, down)) return (IntPtr)1;
                }
            }
            catch { /* un hook ne doit jamais lever */ }
        }
        return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private static char ModOf(int vk) => vk switch
    {
        0x5B or 0x5C => 'W',
        0x11 or 0xA2 or 0xA3 => 'C',
        0x12 or 0xA4 or 0xA5 => 'A',
        0x10 or 0xA0 or 0xA1 => 'S',
        _ => '\0',
    };

    /// <returns>true pour avaler l'évènement.</returns>
    private bool Handle(int vk, bool down)
    {
        if (vk == VK_MASK) return false;
        if (_capture != null) return HandleCapture(vk, down);
        var spec = _spec;
        if (spec == null) return false;

        char mod = ModOf(vk);
        if (mod != '\0')
        {
            switch (mod)
            {
                case 'W': _win = down; break;
                case 'C': _ctrl = down; break;
                case 'A': _alt = down; break;
                case 'S': _shift = down; break;
            }

            if (down)
            {
                if (spec.Vk == 0 && !_other && HeldEquals(spec))
                {
                    if (!_armed) InjectMask(); // Win/Alt ont "servi" : ni menu Démarrer ni menu de la fenêtre au relâchement (une seule fois malgré la répétition de touche)
                    _armed = true;
                }
                else if (!HeldSubsetOf(spec)) { _other = true; _armed = false; }
            }
            else
            {
                if (_armed)
                {
                    _armed = false;
                    _other = true; // pas de second déclenchement tant que tout n'est pas relâché
                    _dispatcher.BeginInvoke(_onTrigger);
                }
                if (!_win && !_ctrl && !_alt && !_shift) _other = false;
            }
            return false;
        }

        // touche normale
        if (!down) return _swallowUp.Remove(vk);

        if (_win || _ctrl || _alt || _shift) { _other = true; _armed = false; }
        if (spec.Vk != 0 && vk == spec.Vk && HeldEquals(spec))
        {
            InjectMask();
            _swallowUp.Add(vk);
            _dispatcher.BeginInvoke(_onTrigger);
            return true;
        }
        return false;
    }

    private bool HeldEquals(HotkeySpec s) => _win == s.Win && _ctrl == s.Ctrl && _alt == s.Alt && _shift == s.Shift;

    private bool HeldSubsetOf(HotkeySpec s) =>
        (!_win || s.Win) && (!_ctrl || s.Ctrl) && (!_alt || s.Alt) && (!_shift || s.Shift);

    private bool HandleCapture(int vk, bool down)
    {
        char mod = ModOf(vk);
        if (down)
        {
            if (vk == 0x1B && _capHeld.Count == 0) { _dispatcher.BeginInvoke(() => FinishCapture(null, null)); return true; } // Échap
            _capHeld.Add(vk);
            switch (mod)
            {
                case 'W': _capWin = true; break;
                case 'C': _capCtrl = true; break;
                case 'A': _capAlt = true; break;
                case 'S': _capShift = true; break;
                default: _capKey = vk; break;
            }
            return true;
        }

        bool wasHeld = _capHeld.Remove(vk);
        if (wasHeld && _capHeld.Count == 0)
        {
            var spec = new HotkeySpec { Win = _capWin, Ctrl = _capCtrl, Alt = _capAlt, Shift = _capShift, Vk = _capKey };
            string text = spec.ToString();
            bool ok = HotkeySpec.Parse(text) != null;
            _dispatcher.BeginInvoke(() => FinishCapture(ok ? text : null,
                ok ? null : "Combinaison invalide : utilise au moins deux modificateurs (ex. Win+Alt) ou un modificateur + une touche."));
        }
        return true;
    }

    private static void InjectMask()
    {
        var inputs = new Native.INPUT[2];
        inputs[0].type = 1;
        inputs[0].u.ki = new Native.KEYBDINPUT { wVk = VK_MASK };
        inputs[1].type = 1;
        inputs[1].u.ki = new Native.KEYBDINPUT { wVk = VK_MASK, dwFlags = Native.KEYEVENTF_KEYUP };
        Native.SendInput(2, inputs, Marshal.SizeOf<Native.INPUT>());
    }

    public void Dispose()
    {
        _capTimer?.Stop();
        if (_hook != IntPtr.Zero) Native.UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }
}
