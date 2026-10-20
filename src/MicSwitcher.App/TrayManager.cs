using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using MicSwitcher.Core;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace MicSwitcher.App;

/// <summary>System tray icon: shows which mic is active and offers pause / open / exit.</summary>
public sealed class TrayManager : IDisposable
{
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Forms.ToolStripMenuItem _pauseItem;
    private readonly Dictionary<string, Drawing.Icon> _icons = new();
    private readonly Action _openWindow;
    private readonly Action<bool> _setPaused;
    private readonly Action _exit;

    public TrayManager(Action openWindow, Action<bool> setPaused, Action exit, bool paused)
    {
        _openWindow = openWindow;
        _setPaused = setPaused;
        _exit = exit;

        _pauseItem = new Forms.ToolStripMenuItem("Pause automatic switching") { Checked = paused, CheckOnClick = true };
        _pauseItem.CheckedChanged += (_, _) => _setPaused(_pauseItem.Checked);

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open MicSwitcher", null, (_, _) => _openWindow());
        menu.Items.Add(_pauseItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => _exit());

        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = GetIcon(Drawing.Color.Gray),
            Text = "MicSwitcher",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
            {
                _openWindow();
            }
        };
    }

    public void SetPaused(bool paused)
    {
        if (_pauseItem.Checked != paused)
        {
            _pauseItem.Checked = paused;
        }
    }

    public void Update(ControllerStatus status)
    {
        (string label, Drawing.Color color) = status.ActiveRole switch
        {
            MicRole.Pro => ("Pro mic", Drawing.Color.FromArgb(46, 160, 67)),
            MicRole.Headset => ("Headset mic", Drawing.Color.FromArgb(219, 143, 0)),
            MicRole.Laptop => ("Laptop mic", Drawing.Color.FromArgb(207, 34, 46)),
            _ => ("No managed mic", Drawing.Color.Gray),
        };

        if (status.Paused)
        {
            color = Drawing.Color.Gray;
            label += " (paused)";
        }

        _notifyIcon.Icon = GetIcon(color);

        // NotifyIcon.Text is limited to 63 characters.
        string text = $"MicSwitcher: {label}";
        _notifyIcon.Text = text.Length <= 63 ? text : text[..63];
    }

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
