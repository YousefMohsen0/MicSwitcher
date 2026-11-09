using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MicSwitcher.App;

/// <summary>One entry in a mic drop-down. A null <see cref="Id"/> means "not set".</summary>
public sealed record DeviceItem(string? Id, string Name);

/// <summary>How the status badge of a mic row is coloured.</summary>
public enum BadgeKind
{
    Muted,
    Good,
    Problem,
}

/// <summary>Device picker + live level meter (with a voice-threshold marker) for one mic role.</summary>
public partial class MicRow : UserControl
{
    private const double MinDb = -80;

    private double? _thresholdDb;

    public MicRow()
    {
        InitializeComponent();
    }

    public ComboBox Combo => DeviceCombo;

    public void Configure(string title, string subtitle, Wpf.Ui.Controls.SymbolRegular icon)
    {
        TitleText.Text = title;
        SubtitleText.Text = subtitle;
        RoleIcon.Symbol = icon;
    }

    /// <summary>Raised when the user clicks the pencil to rename the mic or change its icon.</summary>
    public event EventHandler? EditRequested;

    private void EditButton_Click(object sender, RoutedEventArgs e) => EditRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Shows the user's own name and icon (a picture wins over a built-in symbol).</summary>
    public void SetAppearance(string title, Wpf.Ui.Controls.SymbolRegular symbol, ImageSource? image)
    {
        TitleText.Text = title;
        RoleIcon.Symbol = symbol;
        RoleImage.Source = image;
        RoleImage.Visibility = image is null ? Visibility.Collapsed : Visibility.Visible;
        RoleIcon.Visibility = image is null ? Visibility.Visible : Visibility.Collapsed;
    }

    public void SetLevel(double db)
    {
        Meter.Value = Math.Clamp(db, MinDb, 0);
        LevelText.Text = db <= -99 ? "-∞ dB" : $"{db:0} dB";
    }

    public void ClearLevel()
    {
        Meter.Value = MinDb;
        LevelText.Text = "—";
    }

    /// <summary>Shows (or hides, when null) the voice-threshold marker on the meter.</summary>
    public void SetThreshold(double? db)
    {
        _thresholdDb = db;
        PositionMarker();
    }

    public void SetBadge(string text, BadgeKind kind)
    {
        BadgeText.Text = text;
        string key = kind switch
        {
            BadgeKind.Good => "SystemFillColorSuccessBrush",
            BadgeKind.Problem => "SystemFillColorCriticalBrush",
            _ => "TextFillColorSecondaryBrush",
        };

        BadgeText.Foreground = TryFindResource(key) as Brush
            ?? (kind switch
            {
                BadgeKind.Good => Brushes.MediumSeaGreen,
                BadgeKind.Problem => Brushes.IndianRed,
                _ => Brushes.Gray,
            });
    }

    private void MeterHost_SizeChanged(object sender, SizeChangedEventArgs e) => PositionMarker();

    private void PositionMarker()
    {
        if (_thresholdDb is not double db)
        {
            ThresholdMarker.Visibility = Visibility.Collapsed;
            return;
        }

        double fraction = Math.Clamp((db - MinDb) / -MinDb, 0, 1);
        double x = Math.Max(0, MeterHost.ActualWidth * fraction - 1);
        ThresholdMarker.Margin = new Thickness(x, 0, 0, 0);
        ThresholdMarker.Visibility = Visibility.Visible;
    }
}
