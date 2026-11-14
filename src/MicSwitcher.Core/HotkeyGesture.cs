namespace MicSwitcher.Core;

[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
    Windows = 8,
}

/// <summary>Reads and writes shortcuts such as "Ctrl+Alt+F1". The key part is a WPF key name.</summary>
public static class HotkeyGesture
{
    /// <summary>A key is acceptable alone only if it cannot be typed by accident.</summary>
    private static bool IsStandalone(string key) =>
        key.Length >= 2 && key[0] == 'F' && int.TryParse(key.AsSpan(1), out int n) && n is >= 1 and <= 24;

    public static bool TryParse(string? text, out HotkeyModifiers modifiers, out string key)
    {
        modifiers = HotkeyModifiers.None;
        key = string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        foreach (string raw in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers |= HotkeyModifiers.Control; break;
                case "alt": modifiers |= HotkeyModifiers.Alt; break;
                case "shift": modifiers |= HotkeyModifiers.Shift; break;
                case "win" or "windows": modifiers |= HotkeyModifiers.Windows; break;
                default:
                    if (key.Length > 0)
                    {
                        return false; // two non-modifier keys
                    }

                    key = raw.Length == 1 ? raw.ToUpperInvariant() : char.ToUpperInvariant(raw[0]) + raw[1..];
                    break;
            }
        }

        return key.Length > 0 && (modifiers != HotkeyModifiers.None || IsStandalone(key));
    }

    public static string Format(HotkeyModifiers modifiers, string key)
    {
        var parts = new List<string>();
        if (modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(HotkeyModifiers.Windows)) parts.Add("Win");
        parts.Add(key);
        return string.Join("+", parts);
    }

    /// <summary>The canonical text of a gesture, or null when it is not valid.</summary>
    public static string? Normalize(string? text) =>
        TryParse(text, out HotkeyModifiers m, out string k) ? Format(m, k) : null;
}
