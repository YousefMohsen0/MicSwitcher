using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using MicSwitcher.Core;

namespace MicSwitcher.App;

/// <summary>Registers global keyboard shortcuts with Windows and reports when one is pressed.</summary>
public sealed class HotkeyManager : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const int ModNoRepeat = 0x4000;
    private static readonly IntPtr MessageOnlyParent = new(-3);

    private readonly HwndSource _source;
    private readonly Dictionary<int, HotkeyBinding> _registered = new();
    private int _nextId = 1;

    public HotkeyManager()
    {
        var parameters = new HwndSourceParameters("MicSwitcherHotkeys") { ParentWindow = MessageOnlyParent, Width = 0, Height = 0 };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
    }

    public event Action<HotkeyBinding>? Pressed;

    /// <summary>Replaces all shortcuts. Returns the gestures Windows refused (already used by another program).</summary>
    public IReadOnlyList<string> Register(IEnumerable<HotkeyBinding> bindings)
    {
        UnregisterAll();
        var failed = new List<string>();
        foreach (HotkeyBinding binding in bindings)
        {
            if (!HotkeyGesture.TryParse(binding.Gesture, out HotkeyModifiers modifiers, out string keyName)
                || !TryKeyFromName(keyName, out Key key))
            {
                failed.Add(binding.Gesture);
                continue;
            }

            int id = _nextId++;
            int vk = KeyInterop.VirtualKeyFromKey(key);
            if (RegisterHotKey(_source.Handle, id, (int)modifiers | ModNoRepeat, vk))
            {
                _registered[id] = binding;
            }
            else
            {
                failed.Add(binding.Gesture);
            }
        }

        return failed;
    }

    public static bool TryKeyFromName(string name, out Key key)
    {
        if (name.Length == 1 && char.IsDigit(name[0]))
        {
            name = "D" + name;
        }

        return Enum.TryParse(name, ignoreCase: true, out key) && key != Key.None;
    }

    /// <summary>The name used in gestures for a key pressed by the user, or null when it cannot be used on its own.</summary>
    public static string? NameFromKey(Key key)
    {
        switch (key)
        {
            case Key.None or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift
                or Key.RightShift or Key.LWin or Key.RWin or Key.System or Key.DeadCharProcessed or Key.ImeProcessed:
                return null;
        }

        string text = key.ToString();
        return text.Length == 2 && text[0] == 'D' && char.IsDigit(text[1]) ? text[1].ToString() : text;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && _registered.TryGetValue(wParam.ToInt32(), out HotkeyBinding? binding))
        {
            handled = true;
            Pressed?.Invoke(binding);
        }

        return IntPtr.Zero;
    }

    private void UnregisterAll()
    {
        foreach (int id in _registered.Keys)
        {
            UnregisterHotKey(_source.Handle, id);
        }

        _registered.Clear();
    }

    public void Dispose()
    {
        UnregisterAll();
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, int modifiers, int vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
