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

    public event EventHandler? MoveUpRequested;
    public event EventHandler? MoveDownRequested;
    public event EventHandler? RemoveRequested;

    /// <summary>Raised when the user moves the threshold slider (not when it is set from code).</summary>
    public event Action<double>? ThresholdChanged;

    private bool _settingSlider;

    private void UpButton_Click(object sender, RoutedEventArgs e) => MoveUpRequested?.Invoke(this, EventArgs.Empty);
    private void DownButton_Click(object sender, RoutedEventArgs e) => MoveDownRequested?.Invoke(this, EventArgs.Empty);
    private void RemoveButton_Click(object sender, RoutedEventArgs e) => RemoveRequested?.Invoke(this, EventArgs.Empty);

    private void ThresholdSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        ThresholdText.Text = $"{ThresholdSlider.Value:0} dB";
        if (!_settingSlider)
        {
            ThresholdChanged?.Invoke(ThresholdSlider.Value);
        }

        SetThreshold(ThresholdSlider.Value);
    }

    /// <summary>Sets the slider (and the marker on the meter) without raising <see cref="ThresholdChanged"/>.</summary>
    public void SetThresholdValue(double db)
    {
        _settingSlider = true;
        ThresholdSlider.Value = Math.Clamp(db, ThresholdSlider.Minimum, ThresholdSlider.Maximum);
        _settingSlider = false;
        ThresholdText.Text = $"{ThresholdSlider.Value:0} dB";
        SetThreshold(ThresholdSlider.Value);
    }

    public void SetSubtitle(string text) => SubtitleText.Text = text;

    public void SetMoveEnabled(bool up, bool down)
    {
        UpButton.IsEnabled = up;
        DownButton.IsEnabled = down;
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
