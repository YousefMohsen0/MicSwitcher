using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using MicSwitcher.Core;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace MicSwitcher.App;

/// <summary>What the tray menu can ask the rest of the app to do.</summary>
public sealed record TrayActions(
    Action Open,
    Action<bool> SetPaused,
    Action Exit,
    Action<string> SwitchProfile,
    Action<string, TimeSpan> SwitchToMic,
    Action ReleaseLock);

/// <summary>
/// System tray icon: shows which mic is active and offers a quick switch menu (mics, profiles, pause, open, exit).
/// The menu is rebuilt every time it opens, so it always reflects the current mics and profiles.
/// </summary>
public sealed class TrayManager : IDisposable
{
    private static readonly (int Minutes, string Key)[] LockChoices =
    {
        (0, "Tray_LockUntilUnlock"), (10, "Tray_Lock10"), (30, "Tray_Lock30"), (60, "Tray_Lock60"),
    };

    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Forms.ContextMenuStrip _menu;
    private readonly Dictionary<string, Drawing.Icon> _icons = new();
    private readonly AppSettings _settings;
    private readonly SwitchController _controller;
    private readonly TrayActions _actions;
    private int _lockMinutes;

    public TrayManager(AppSettings settings, SwitchController controller, TrayActions actions)
    {
        _settings = settings;
        _controller = controller;
        _actions = actions;

        _menu = new Forms.ContextMenuStrip
        {
            Font = new Drawing.Font("Segoe UI", 9.5f),
            ShowImageMargin = false,
            ShowCheckMargin = false,
            Padding = new Forms.Padding(0, 6, 0, 6),
            RightToLeft = Loc.Language.RightToLeft ? Forms.RightToLeft.Yes : Forms.RightToLeft.No,
        };
        _menu.Opening += (_, _) => Rebuild();
        _menu.HandleCreated += (_, _) => RoundCorners(_menu.Handle);

        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = GetIcon(Drawing.Color.Gray),
            Text = "MicSwitcher",
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
            {
                _actions.Open();
            }
        };

        Rebuild();
    }

    /// <summary>Shows a small notification next to the tray icon.</summary>
    public void Notify(string title, string text)
    {
        try
        {
            _notifyIcon.ShowBalloonTip(4000, title, text, Forms.ToolTipIcon.None);
        }
        catch (Exception ex)
        {
            AppLog.Info($"Could not show a notification: {ex.Message}");
        }
    }

    public void Update(ControllerStatus status)
    {
        int index = status.ActiveMicId is null ? -1 : _settings.IndexOfMic(status.ActiveMicId);
        string label = index < 0 ? Loc.T("Tray_NoMic") : _settings.MicName(status.ActiveMicId!);
        Drawing.Color color = index < 0 ? Drawing.Color.Gray : ColorAt(index);

        if (status.Paused)
        {
            color = Drawing.Color.Gray;
            label += " " + Loc.T("Tray_PausedSuffix");
        }
        else if (status.LockedMicId is not null)
        {
            label += " " + Loc.T("Tray_LockedSuffix");
        }

        _notifyIcon.Icon = GetIcon(color);

        // NotifyIcon.Text is limited to 63 characters.
        string text = $"MicSwitcher: {label}";
        _notifyIcon.Text = text.Length <= 63 ? text : text[..63];
    }

    private static Drawing.Color ColorAt(int index)
    {
        (byte r, byte g, byte b) = MicColors.At(index);
        return Drawing.Color.FromArgb(r, g, b);
    }

    private void Rebuild()
    {
        ControllerStatus? status = _controller.LastStatus;
        _menu.Items.Clear();

        string header = "MicSwitcher";
        if (status?.ActiveMicId is string active)
        {
            header = _settings.MicName(active);
        }

        var headerItem = new Forms.ToolStripMenuItem(header) { Enabled = false, Font = new Drawing.Font("Segoe UI Semibold", 9.5f) };
        _menu.Items.Add(headerItem);
        _menu.Items.Add(new Forms.ToolStripSeparator());

        TimeSpan lockFor = _lockMinutes == 0 ? TimeSpan.MaxValue : TimeSpan.FromMinutes(_lockMinutes);
        for (int i = 0; i < _settings.Mics.Count; i++)
        {
            MicSlot mic = _settings.Mics[i];
            MicStatus? state = status?.Find(mic.Id);
            bool present = state?.Present == true;
            bool isActive = status?.ActiveMicId == mic.Id;
            string text = (isActive ? "●  " : "     ") + mic.DisplayName(i) + (present || mic.DeviceId is null ? string.Empty : "  — " + Loc.T("Tray_NotFound"));
            var item = new Forms.ToolStripMenuItem(text) { Enabled = present };
            string id = mic.Id;
            item.Click += (_, _) => _actions.SwitchToMic(id, lockFor);
            _menu.Items.Add(item);
        }

        if (_settings.Mics.Count > 0)
        {
            var keep = new Forms.ToolStripMenuItem(Loc.T("Tray_KeepFor"));
            foreach ((int minutes, string key) in LockChoices)
            {
                int m = minutes;
                var choice = new Forms.ToolStripMenuItem((_lockMinutes == m ? "●  " : "     ") + Loc.T(key));
                choice.Click += (_, _) => _lockMinutes = m;
                keep.DropDownItems.Add(choice);
            }

            _menu.Items.Add(keep);

            if (status?.LockedMicId is not null)
            {
                _menu.Items.Add(Loc.T("Tray_BackToAuto"), null, (_, _) => _actions.ReleaseLock());
            }

            _menu.Items.Add(new Forms.ToolStripSeparator());
        }

        if (_settings.Profiles.Count > 1)
        {
            var profiles = new Forms.ToolStripMenuItem(Loc.T("Tray_Profile"));
            foreach (Profile profile in _settings.Profiles)
            {
                string id = profile.Id;
                var item = new Forms.ToolStripMenuItem((_settings.ActiveProfileId == id ? "●  " : "     ") + profile.Name);
                item.Click += (_, _) => _actions.SwitchProfile(id);
                profiles.DropDownItems.Add(item);
            }

            _menu.Items.Add(profiles);
        }

        bool paused = _settings.Paused;
        _menu.Items.Add(Loc.T(paused ? "Tray_Resume" : "Tray_Pause"), null, (_, _) => _actions.SetPaused(!paused));
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add(Loc.T("Tray_Open"), null, (_, _) => _actions.Open());
        _menu.Items.Add(Loc.T("Tray_Exit"), null, (_, _) => _actions.Exit());

        foreach (Forms.ToolStripItem item in _menu.Items)
        {
            item.Padding = new Forms.Padding(2, 5, 2, 5);
        }

        ApplyTheme(_menu);
        foreach (Forms.ToolStripItem item in _menu.Items)
        {
            if (item is Forms.ToolStripMenuItem sub && sub.HasDropDownItems)
            {
                ApplyTheme(sub.DropDown);
            }
        }
    }

    // ----- look: follows the Windows light/dark setting, flat with rounded corners on Windows 11 -----------------

    private static void ApplyTheme(Forms.ToolStripDropDown menu)
    {
        bool light = AppTheme.IsLight;
        bool black = AppTheme.IsBlack;
        var colors = black
            ? new MenuColors(Drawing.Color.FromArgb(10, 10, 10), Drawing.Color.FromArgb(255, 255, 255),
                Drawing.Color.FromArgb(140, 140, 140), Drawing.Color.FromArgb(40, 40, 40), Drawing.Color.FromArgb(48, 48, 48))
            : light
            ? new MenuColors(Drawing.Color.FromArgb(249, 249, 249), Drawing.Color.FromArgb(32, 32, 32),
                Drawing.Color.FromArgb(120, 120, 120), Drawing.Color.FromArgb(232, 232, 232), Drawing.Color.FromArgb(220, 220, 220))
            : new MenuColors(Drawing.Color.FromArgb(44, 44, 44), Drawing.Color.FromArgb(255, 255, 255),
                Drawing.Color.FromArgb(150, 150, 150), Drawing.Color.FromArgb(66, 66, 66), Drawing.Color.FromArgb(70, 70, 70));

        menu.Renderer = new Forms.ToolStripProfessionalRenderer(new MenuColorTable(colors)) { RoundedEdges = false };
        menu.BackColor = colors.Back;
        menu.ForeColor = colors.Text;
        foreach (Forms.ToolStripItem item in menu.Items)
        {
            item.ForeColor = item.Enabled ? colors.Text : colors.Muted;
            item.BackColor = colors.Back;
        }
    }

    private sealed record MenuColors(Drawing.Color Back, Drawing.Color Text, Drawing.Color Muted, Drawing.Color Hover, Drawing.Color Line);

    private sealed class MenuColorTable : Forms.ProfessionalColorTable
    {
        private readonly MenuColors _c;

        public MenuColorTable(MenuColors colors)
        {
            _c = colors;
            UseSystemColors = false;
        }

        public override Drawing.Color ToolStripDropDownBackground => _c.Back;
        public override Drawing.Color MenuBorder => _c.Line;
        public override Drawing.Color MenuItemBorder => _c.Hover;
        public override Drawing.Color MenuItemSelected => _c.Hover;
        public override Drawing.Color MenuItemSelectedGradientBegin => _c.Hover;
        public override Drawing.Color MenuItemSelectedGradientEnd => _c.Hover;
        public override Drawing.Color ImageMarginGradientBegin => _c.Back;
        public override Drawing.Color ImageMarginGradientMiddle => _c.Back;
        public override Drawing.Color ImageMarginGradientEnd => _c.Back;
        public override Drawing.Color SeparatorDark => _c.Line;
        public override Drawing.Color SeparatorLight => _c.Back;
        public override Drawing.Color CheckBackground => _c.Hover;
        public override Drawing.Color CheckSelectedBackground => _c.Hover;
        public override Drawing.Color CheckPressedBackground => _c.Hover;
    }

    private static void RoundCorners(IntPtr handle)
    {
        try
        {
            int preference = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(handle, 33, ref preference, sizeof(int));
        }
        catch (Exception)
        {
            // Older Windows: square corners are fine.
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private Drawing.Icon GetIcon(Drawing.Color color)
    {
        string key = color.ToArgb().ToString();
        if (_icons.TryGetValue(key, out Drawing.Icon? cached))
        {
            return cached;
        }

        Drawing.Icon icon = CreateMicIcon(color);
        _icons[key] = icon;
        return icon;
    }

    /// <summary>Draws a small microphone glyph on a coloured disc (no icon resource needed).</summary>
    private static Drawing.Icon CreateMicIcon(Drawing.Color background)
    {
        using var bitmap = new Drawing.Bitmap(32, 32);
        using (Drawing.Graphics g = Drawing.Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Drawing.Color.Transparent);

            using var disc = new Drawing.SolidBrush(background);
            g.FillEllipse(disc, 1, 1, 30, 30);

            using var white = new Drawing.SolidBrush(Drawing.Color.White);
            g.FillEllipse(white, 12, 5, 8, 8);     // top of the capsule
            g.FillRectangle(white, 12, 9, 8, 7);   // body
            g.FillEllipse(white, 12, 12, 8, 8);    // bottom of the capsule

            using var pen = new Drawing.Pen(Drawing.Color.White, 2f);
            g.DrawArc(pen, 9, 9, 14, 14, 0, 180);  // holder
            g.DrawLine(pen, 16, 23, 16, 26);       // stem
            g.DrawLine(pen, 12, 26, 20, 26);       // base
        }

        IntPtr handle = bitmap.GetHicon();
        try
        {
            using Drawing.Icon temporary = Drawing.Icon.FromHandle(handle);
            return (Drawing.Icon)temporary.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        foreach (Drawing.Icon icon in _icons.Values)
        {
            icon.Dispose();
        }
    }
}
