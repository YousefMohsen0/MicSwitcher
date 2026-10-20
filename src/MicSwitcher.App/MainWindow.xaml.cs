using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using MicSwitcher.App.Audio;
using MicSwitcher.Core;

namespace MicSwitcher.App;

/// <summary>Settings window: mic selection, live meters, calibration, tuning and the activity log.</summary>
public partial class MainWindow : Window
{
    private const string MissingSuffix = " (not connected)";

    private readonly AppSettings _settings;
    private readonly SwitchController _controller;
    private readonly AudioDeviceService _devices;
    private readonly Action _persist;
    private readonly Action<bool> _setPaused;
    private readonly Dictionary<MicRole, MicRow> _rows;
    private readonly ObservableCollection<string> _logLines = new();
    private readonly DispatcherTimer _deviceTimer;
    private readonly object _calibrationLock = new();

    // True while the UI is being filled from settings, so change handlers do not write back.
    private bool _loading = true;
    private bool _allowClose;
    private volatile bool _visible;
    private string _deviceSignature = string.Empty;
    private List<double>? _calibrationSink;

    public MainWindow(
        AppSettings settings,
        SwitchController controller,
        AudioDeviceService devices,
        Action persist,
        Action<bool> setPaused)
    {
        _settings = settings;
        _controller = controller;
        _devices = devices;
        _persist = persist;
        _setPaused = setPaused;

        InitializeComponent();

        _rows = new Dictionary<MicRole, MicRow>
        {
            [MicRole.Pro] = ProRow,
            [MicRole.Headset] = HeadsetRow,
            [MicRole.Laptop] = LaptopRow,
        };

        ProRow.Title = "Pro mic (main)";
        HeadsetRow.Title = "Headset mic";
        LaptopRow.Title = "Laptop mic (only if the others are missing)";

        foreach ((MicRole role, MicRow row) in _rows)
        {
            row.Combo.SelectionChanged += (_, _) => OnDeviceSelected(role);
        }

        LogList.ItemsSource = _logLines;
        foreach (string line in AppLog.Snapshot())
        {
            _logLines.Add(line);
        }

        AppLog.LineAdded += OnLogLine;
        _controller.LevelMeasured += OnLevelMeasured;

        _deviceTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _deviceTimer.Tick += (_, _) => RefreshDeviceLists();

        IsVisibleChanged += OnVisibleChanged;

        LoadSettingsIntoControls();
        RefreshDeviceLists();
        if (_controller.LastStatus is ControllerStatus status)
        {
            UpdateStatus(status);
        }

        _loading = false;
    }

    public void AllowClose() => _allowClose = true;

    /// <summary>Closing the window hides it; the app keeps running in the tray.</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            Hide();
        }
        else
        {
            AppLog.LineAdded -= OnLogLine;
            _controller.LevelMeasured -= OnLevelMeasured;
        }

        base.OnClosing(e);
    }

    // ----- visibility / refresh -------------------------------------------------------------------------------

    private void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        _visible = IsVisible;
        _controller.SetMetersWanted(_visible);

        if (_visible)
        {
            _deviceSignature = string.Empty;
            RefreshDeviceLists();
            _deviceTimer.Start();
        }
        else
        {
            _deviceTimer.Stop();
            foreach (MicRow row in _rows.Values)
            {
                row.ClearLevel();
            }
        }
    }

    private void LoadSettingsIntoControls()
    {
        bool wasLoading = _loading;
        _loading = true;
        try
        {
            PauseCheck.IsChecked = _settings.Paused;
            AdjustGainCheck.IsChecked = _settings.AdjustGainOnCalibrate;
            CommsCheck.IsChecked = _settings.SwitchCommunicationsRole;
            StartupCheck.IsChecked = _settings.StartWithWindows;

            ThresholdSlider.Value = _settings.VoiceThresholdDb;
            SilenceSlider.Value = _settings.SilenceTimeoutSeconds;
            ReturnSlider.Value = _settings.ReturnDelayMs;
            UpdateSliderLabels();
        }
        finally
        {
            _loading = wasLoading;
        }
    }

    private void UpdateSliderLabels()
    {
        ThresholdValue.Text = $"{ThresholdSlider.Value:0} dB";
        SilenceValue.Text = $"{SilenceSlider.Value:0.#} s";
        ReturnValue.Text = $"{ReturnSlider.Value:0} ms";
    }

    private void RefreshDeviceLists()
    {
        if (_rows.Values.Any(r => r.Combo.IsDropDownOpen))
        {
            return; // do not yank the list away while the user is choosing
        }

        IReadOnlyList<CaptureDevice> devices;
        try
        {
            devices = _devices.GetActiveCaptureDevices();
        }
        catch (Exception)
        {
            return;
        }

        string signature = string.Join("|", devices.Select(d => d.Id))
            + "#" + string.Join("|", _rows.Keys.Select(r => _settings.Get(r).DeviceId));
        if (signature == _deviceSignature)
        {
            return;
        }

        _deviceSignature = signature;

        bool wasLoading = _loading;
        _loading = true;
        try
        {
            foreach (MicRole role in _rows.Keys)
            {
                PopulateCombo(role, devices);
            }
        }
        finally
        {
            _loading = wasLoading;
        }
    }

    private void PopulateCombo(MicRole role, IReadOnlyList<CaptureDevice> devices)
    {
        var combo = _rows[role].Combo;
        MicSettings saved = _settings.Get(role);

        var items = new List<DeviceItem> { new(null, "(not set)") };
        items.AddRange(devices.Select(d => new DeviceItem(d.Id, d.Name)));
        if (saved.DeviceId is not null && devices.All(d => d.Id != saved.DeviceId))
        {
            items.Add(new DeviceItem(saved.DeviceId, (saved.DeviceName ?? "Unknown device") + MissingSuffix));
        }

        combo.ItemsSource = items;
        combo.SelectedItem = items.FirstOrDefault(i => i.Id == saved.DeviceId) ?? items[0];
    }

    private void OnDeviceSelected(MicRole role)
    {
        if (_loading || _rows[role].Combo.SelectedItem is not DeviceItem item)
        {
            return;
        }

        MicSettings target = _settings.Get(role);
        if (item.Id == target.DeviceId)
        {
            return;
        }

        target.DeviceId = item.Id;
        target.DeviceName = item.Id is null ? null : item.Name;

        if (item.Id is not null)
        {
            foreach (MicRole other in _rows.Keys.Where(r => r != role))
            {
                if (_settings.Get(other).DeviceId == item.Id)
                {
                    AppLog.Info($"Warning: the same device is assigned to the {role} and {other} mics.");
                }
            }
        }

        AppLog.Info($"{role} mic set to: {item.Name}");
        _deviceSignature = string.Empty;
        _persist();
    }

    // ----- live data from the controller ----------------------------------------------------------------------

    private void OnLevelMeasured(MicRole role, double db)
    {
        if (role == MicRole.Pro)
        {
            lock (_calibrationLock)
            {
                _calibrationSink?.Add(db);
            }
        }

        if (!_visible || Dispatcher.HasShutdownStarted)
        {
            return;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (_rows.TryGetValue(role, out MicRow? row))
            {
                row.SetLevel(db);
            }
        }));
    }

    private void OnLogLine(string line)
    {
        if (Dispatcher.HasShutdownStarted)
        {
            return;
        }

        Dispatcher.BeginInvoke(new Action(() =>
        {
            _logLines.Add(line);
            while (_logLines.Count > 200)
            {
                _logLines.RemoveAt(0);
            }

            LogList.ScrollIntoView(line);
        }));
    }

    public void UpdateStatus(ControllerStatus status)
    {
        ActiveText.Text = status.ActiveRole switch
        {
            MicRole.Pro => "Active mic: Pro mic",
            MicRole.Headset => "Active mic: Headset mic",
            MicRole.Laptop => "Active mic: Laptop mic",
            _ => "Active mic: none of your managed mics",
        };

        bool nothingConfigured = _rows.Keys.All(r => _settings.Get(r).DeviceId is null);
        DetailText.Text = nothingConfigured
            ? "Choose your microphones below to get started."
            : status.Message;

        SetBadge(ProRow, MicRole.Pro, status.ProPresent, status.ProVoice ? "voice" : "quiet", status);
        SetBadge(HeadsetRow, MicRole.Headset, status.HeadsetPresent, "connected", status);
        SetBadge(LaptopRow, MicRole.Laptop, status.LaptopPresent, "connected", status);
        SyncPaused();
    }

    private void SetBadge(MicRow row, MicRole role, bool present, string presentText, ControllerStatus status)
    {
        bool configured = _settings.Get(role).DeviceId is not null;
        if (!configured)
        {
            row.SetBadge(string.Empty, good: false);
            return;
        }

        if (!present)
        {
            row.SetBadge("not found", good: false, problem: true);
            return;
        }

        string prefix = status.ActiveRole == role ? "★ default · " : string.Empty;
        row.SetBadge(prefix + presentText, good: true);
    }

    public void SyncPaused()
    {
        bool wasLoading = _loading;
        _loading = true;
        PauseCheck.IsChecked = _settings.Paused;
        _loading = wasLoading;
    }

    // ----- option handlers ------------------------------------------------------------------------------------

    private void PauseCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _setPaused(PauseCheck.IsChecked == true);
    }

    private void AdjustGainCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _settings.AdjustGainOnCalibrate = AdjustGainCheck.IsChecked == true;
        _persist();
    }

    private void CommsCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _settings.SwitchCommunicationsRole = CommsCheck.IsChecked == true;
        _persist();
    }

    private void ThresholdSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading)
        {
            return;
        }

        _settings.VoiceThresholdDb = Math.Round(ThresholdSlider.Value, 1);
        UpdateSliderLabels();
        _persist();
    }

    private void SilenceSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading)
        {
            return;
        }

        _settings.SilenceTimeoutSeconds = Math.Round(SilenceSlider.Value, 1);
        UpdateSliderLabels();
        _persist();
    }

    private void ReturnSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading)
        {
            return;
        }

        _settings.ReturnDelayMs = Math.Round(ReturnSlider.Value);
        UpdateSliderLabels();
        _persist();
    }

    private async void StartupCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        bool want = StartupCheck.IsChecked == true;
        StartupCheck.IsEnabled = false;
        string? error = await Task.Run(() => StartupManager.SetEnabled(want));
        StartupCheck.IsEnabled = true;

        if (error is null)
        {
            _settings.StartWithWindows = want;
            _persist();
            AppLog.Info(want ? "Start with Windows enabled" : "Start with Windows disabled");
            return;
        }

        AppLog.Info($"Could not change the startup setting: {error}");
        MessageBox.Show(this, error, "MicSwitcher", MessageBoxButton.OK, MessageBoxImage.Warning);

        _loading = true;
        StartupCheck.IsChecked = !want;
        _loading = false;
    }

    // ----- calibration ----------------------------------------------------------------------------------------

    private async void CalibrateButton_Click(object sender, RoutedEventArgs e)
    {
        string? proId = _settings.Pro.DeviceId;
        if (proId is null || _controller.LastStatus?.ProPresent != true)
        {
            MessageBox.Show(
                this,
                "Select your pro mic above (and make sure it is connected) before calibrating.",
                "MicSwitcher",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        CalibrateButton.IsEnabled = false;
        var noise = new List<double>();
        var speech = new List<double>();

        try
        {
            CalibrationText.Text = "Stay quiet for 3 seconds…";
            await CollectAsync(noise, TimeSpan.FromSeconds(3));

            CalibrationText.Text = "Now speak normally, close to the mic, for 6 seconds…";
            await CollectAsync(speech, TimeSpan.FromSeconds(6));

            CalibrationResult result = Calibration.Compute(
                noise, speech, _settings.TargetSpeechDb, _settings.AdjustGainOnCalibrate);

            if (!result.Reliable)
            {
                CalibrationText.Text = result.Warning ?? "Calibration failed.";
                return;
            }

            double appliedGain = 0;
            string gainNote = string.Empty;
            if (_settings.AdjustGainOnCalibrate && Math.Abs(result.GainDeltaDb) >= 0.5)
            {
                try
                {
                    appliedGain = _devices.AdjustGainDb(proId, result.GainDeltaDb);
                    gainNote = $" Input volume changed by {appliedGain:+0.#;-0.#} dB.";
                }
                catch (Exception ex)
                {
                    gainNote = " The input volume could not be changed.";
                    AppLog.Info($"Could not change the input volume: {ex.Message}");
                }
            }

            // The threshold was computed assuming the full gain change; correct it by what was really applied.
            double threshold = result.ThresholdDb - result.GainDeltaDb + appliedGain;
            threshold = Math.Clamp(Math.Round(threshold, 1), ThresholdSlider.Minimum, ThresholdSlider.Maximum);

            _settings.VoiceThresholdDb = threshold;
            _loading = true;
            ThresholdSlider.Value = threshold;
            UpdateSliderLabels();
            _loading = false;
            _persist();

            CalibrationText.Text =
                $"Noise {result.NoiseFloorDb:0} dB, speech {result.SpeechLevelDb:0} dB → threshold {threshold:0} dB.{gainNote}"
                + (result.Warning is null ? string.Empty : " " + result.Warning);
            AppLog.Info($"Calibrated pro mic: noise {result.NoiseFloorDb:0} dB, speech {result.SpeechLevelDb:0} dB, threshold {threshold:0} dB");
        }
        finally
        {
            lock (_calibrationLock)
            {
                _calibrationSink = null;
            }

            CalibrateButton.IsEnabled = true;
        }
    }

    private async Task CollectAsync(List<double> sink, TimeSpan duration)
    {
        lock (_calibrationLock)
        {
            _calibrationSink = sink;
        }

        await Task.Delay(duration);

        lock (_calibrationLock)
        {
            _calibrationSink = null;
        }
    }
}
