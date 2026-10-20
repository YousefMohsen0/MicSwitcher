using System;
using System.Windows.Controls;
using System.Windows.Media;

namespace MicSwitcher.App;

/// <summary>One entry in a mic drop-down. A null <see cref="Id"/> means "not set".</summary>
public sealed record DeviceItem(string? Id, string Name);

/// <summary>Device picker + live level meter for one mic role.</summary>
public partial class MicRow : UserControl
{
    private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66));
    private static readonly Brush Good = new SolidColorBrush(Color.FromRgb(0x2E, 0xA0, 0x43));
    private static readonly Brush Warn = new SolidColorBrush(Color.FromRgb(0xCF, 0x22, 0x2E));

    public MicRow()
    {
        InitializeComponent();
    }

    public string Title
    {
        set => TitleText.Text = value;
    }

    public ComboBox Combo => DeviceCombo;

    public void SetLevel(double db)
    {
        Meter.Value = Math.Clamp(db, -80, 0);
        LevelText.Text = db <= -99 ? "-∞ dB" : $"{db:0} dB";
    }

    public void ClearLevel()
    {
        Meter.Value = -80;
        LevelText.Text = "—";
    }

    public void SetBadge(string text, bool good, bool problem = false)
    {
        BadgeText.Text = text;
        BadgeText.Foreground = problem ? Warn : good ? Good : Muted;
    }
}
