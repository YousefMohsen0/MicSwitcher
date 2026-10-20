using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using MicSwitcher.App.Audio;
using MicSwitcher.Core;

namespace MicSwitcher.App;

/// <summary>Settings window: mic selection, live meters, guided calibration, tuning and the activity log.</summary>
public partial class MainWindow : Wpf.Ui.Controls.FluentWindow
{
    private const string MissingSuffix = " (not connected)";

    private static readonly Brush ProBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0xA0, 0x43));
    private static readonly Brush HeadsetBrush = new SolidColorBrush(Color.FromRgb(0xDB, 0x8F, 0x00));
    private static readonly Brush LaptopBrush = new SolidColorBrush(Color.FromRgb(0xCF, 0x22, 0x2E));
    private static readonly Brush NoneBrush = new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80));

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
    private List<double>? _proSink;
    private List<double>? _headsetSink;
    private CancellationTokenSource? _calibrationCts;

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

        ProRow.Configure(
            "Pro mic",
            "Your main microphone. Used whenever it can hear you.",
            Wpf.Ui.Controls.SymbolRegular.Mic24);
        HeadsetRow.Configure(
            "Headset mic",
            "Takes over when a voice is heard on the headset and not on the pro mic.",
            Wpf.Ui.Controls.SymbolRegular.Headphones24);
        LaptopRow.Configure(
            "Laptop mic",
            "Last resort, only used when neither of the others is connected.",
            Wpf.Ui.Controls.SymbolRegular.Laptop24);

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
        Loaded += (_, _) => Wpf.Ui.Appearance.SystemThemeWatcher.Watch(this);

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
            _calibrationCts?.Cancel();
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
            HeadsetThresholdSlider.Value = _settings.HeadsetVoiceThresholdDb;
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
        HeadsetThresholdValue.Text = $"{HeadsetThresholdSlider.Value:0} dB";
        SilenceValue.Text = $"{SilenceSlider.Value:0.#} s";
        ReturnValue.Text = $"{ReturnSlider.Value:0} ms";

        ProRow.SetThreshold(ThresholdSlider.Value);
        HeadsetRow.SetThreshold(HeadsetThresholdSlider.Value);
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
        lock (_calibrationLock)
        {
            if (role == MicRole.Pro)
            {
                _proSink?.Add(db);
            }
            else if (role == MicRole.Headset)
            {
                _headsetSink?.Add(db);
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
        (string name, Brush dot) = status.ActiveRole switch
        {
            MicRole.Pro => ("Pro mic", ProBrush),
            MicRole.Headset => ("Headset mic", HeadsetBrush),
            MicRole.Laptop => ("Laptop mic", LaptopBrush),
            _ => ("None of your mics", NoneBrush),
        };

        ActiveText.Text = name;
        StatusDot.Fill = status.Paused ? NoneBrush : dot;

        bool nothingConfigured = _rows.Keys.All(r => _settings.Get(r).DeviceId is null);
        DetailText.Text = nothingConfigured
            ? "Choose your microphones below to get started."
            : status.Message;

        SetBadge(ProRow, MicRole.Pro, status.ProPresent, status.ProVoice ? "voice" : "quiet", status);
        SetBadge(HeadsetRow, MicRole.Headset, status.HeadsetPresent, status.HeadsetVoice ? "voice" : "quiet", status);
        SetBadge(LaptopRow, MicRole.Laptop, status.LaptopPresent, "connected", status);
        SyncPaused();
    }

    private void SetBadge(MicRow row, MicRole role, bool present, string presentText, ControllerStatus status)
    {
        if (_settings.Get(role).DeviceId is null)
        {
            row.SetBadge(string.Empty, BadgeKind.Muted);
            return;
        }

        if (!present)
        {
            row.SetBadge("not found", BadgeKind.Problem);
            return;
        }

        string prefix = status.ActiveRole == role ? "★ default · " : string.Empty;
        bool hearingVoice = presentText == "voice";
        row.SetBadge(prefix + presentText, hearingVoice ? BadgeKind.Good : BadgeKind.Muted);
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

    private void HeadsetThresholdSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading)
        {
            return;
        }

        _settings.HeadsetVoiceThresholdDb = Math.Round(HeadsetThresholdSlider.Value, 1);
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

    // ----- guided calibration ---------------------------------------------------------------------------------

    private void CancelCalibrationButton_Click(object sender, RoutedEventArgs e) => _calibrationCts?.Cancel();

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

        bool useHeadset = _settings.Headset.DeviceId is not null && _controller.LastStatus?.HeadsetPresent == true;
        int totalSteps = useHeadset ? 3 : 2;

        var noisePro = new List<double>();
        var noiseHeadset = new List<double>();
        var speechPro = new List<double>();
        var speechHeadset = new List<double>();
        var proWhileHeadsetTalks = new List<double>();

        CalibrateButton.IsEnabled = false;
        CalibrationText.Text = string.Empty;
        CalibrationPanel.Visibility = Visibility.Visible;
        _calibrationCts = new CancellationTokenSource();
        CancellationToken token = _calibrationCts.Token;

        try
        {
            await RunPhaseAsync(
                1, totalSteps, "Stay quiet", "Don't talk. Measuring the background noise.",
                leadInSeconds: 0.5, seconds: 3, token, proSink: noisePro, headsetSink: useHeadset ? noiseHeadset : null);

            await RunPhaseAsync(
                2, totalSteps, "Speak into the pro mic",
                "Say anything at your normal volume, for example count from one to ten.",
                leadInSeconds: 1.5, seconds: 5, token, proSink: speechPro, headsetSink: null);

            if (useHeadset)
            {
                await RunPhaseAsync(
                    3, totalSteps, "Now speak into the headset mic",
                    "Wear the headset and talk the way you will when you use it.",
                    leadInSeconds: 2, seconds: 5, token, proSink: proWhileHeadsetTalks, headsetSink: speechHeadset);
            }

            ApplyCalibration(proId, useHeadset, noisePro, speechPro, proWhileHeadsetTalks, noiseHeadset, speechHeadset);
        }
        catch (OperationCanceledException)
        {
            CalibrationText.Text = "Calibration cancelled. Nothing was changed.";
        }
        finally
        {
            lock (_calibrationLock)
            {
                _proSink = null;
                _headsetSink = null;
            }

            _calibrationCts?.Dispose();
            _calibrationCts = null;
            CalibrationPanel.Visibility = Visibility.Collapsed;
            CalibrateButton.IsEnabled = true;
        }
    }

    /// <summary>Shows a lead-in ("get ready") and then a real countdown while collecting levels into the sinks.</summary>
    private async Task RunPhaseAsync(
        int step,
        int totalSteps,
        string title,
        string hint,
        double leadInSeconds,
        double seconds,
        CancellationToken token,
        List<double>? proSink,
        List<double>? headsetSink)
    {
        CalibrationStepText.Text = $"STEP {step} OF {totalSteps}";
        CalibrationTitleText.Text = title;
        CalibrationHintText.Text = hint;
        CalibrationProgress.Value = 0;

        // Lead-in: gives the user a moment to react; nothing is recorded yet.
        var leadIn = Stopwatch.StartNew();
        CountdownText.FontSize = 40;
        while (leadIn.Elapsed.TotalSeconds < leadInSeconds)
        {
            CountdownText.Text = "Get ready…";
            await Task.Delay(50, token);
        }

        CountdownText.FontSize = 64;
        lock (_calibrationLock)
        {
            _proSink = proSink;
            _headsetSink = headsetSink;
        }

        var timer = Stopwatch.StartNew();
        while (true)
        {
            double elapsed = timer.Elapsed.TotalSeconds;
            if (elapsed >= seconds)
            {
                break;
            }

            CountdownText.Text = Math.Ceiling(seconds - elapsed).ToString("0");
            CalibrationProgress.Value = elapsed / seconds;
            await Task.Delay(50, token);
        }

        CalibrationProgress.Value = 1;
        lock (_calibrationLock)
        {
            _proSink = null;
            _headsetSink = null;
        }
    }

    private void ApplyCalibration(
        string proId,
        bool useHeadset,
        List<double> noisePro,
        List<double> speechPro,
        List<double> proWhileHeadsetTalks,
        List<double> noiseHeadset,
        List<double> speechHeadset)
    {
        CalibrationResult pro = Calibration.Compute(
            noisePro, speechPro, _settings.TargetSpeechDb, _settings.AdjustGainOnCalibrate,
            interferenceLevelsDb: useHeadset ? proWhileHeadsetTalks : null);

        if (!pro.Reliable)
        {
            CalibrationText.Text = "Pro mic: " + (pro.Warning ?? "calibration failed.") + " Nothing was changed.";
            return;
        }

        double appliedGain = 0;
        string gainNote = string.Empty;
        if (_settings.AdjustGainOnCalibrate && Math.Abs(pro.GainDeltaDb) >= 0.5)
        {
            try
            {
                appliedGain = _devices.AdjustGainDb(proId, pro.GainDeltaDb);
                gainNote = $" Input volume changed by {appliedGain:+0.#;-0.#} dB.";
            }
            catch (Exception ex)
            {
                gainNote = " The input volume could not be changed.";
                AppLog.Info($"Could not change the input volume: {ex.Message}");
            }
        }

        // The threshold assumed the full gain change; correct it by what was really applied.
        double proThreshold = Clamp(pro.ThresholdDb - pro.GainDeltaDb + appliedGain, ThresholdSlider);
        _settings.VoiceThresholdDb = proThreshold;

        var lines = new List<string>
        {
            $"Pro mic: noise {pro.NoiseFloorDb:0} dB, speech {pro.SpeechLevelDb:0} dB, threshold {proThreshold:0} dB.{gainNote}",
        };
        if (pro.Warning is not null)
        {
            lines.Add("Pro mic: " + pro.Warning);
        }

        if (useHeadset)
        {
            CalibrationResult headset = Calibration.Compute(
                noiseHeadset, speechHeadset, _settings.TargetSpeechDb, adjustGain: false);

            if (headset.Reliable)
            {
                double headsetThreshold = Clamp(headset.ThresholdDb, HeadsetThresholdSlider);
                _settings.HeadsetVoiceThresholdDb = headsetThreshold;
                lines.Add($"Headset mic: noise {headset.NoiseFloorDb:0} dB, speech {headset.SpeechLevelDb:0} dB, threshold {headsetThreshold:0} dB.");
                if (headset.Warning is not null)
                {
                    lines.Add("Headset mic: " + headset.Warning);
                }
            }
            else
            {
                lines.Add("Headset mic: " + (headset.Warning ?? "calibration failed.") + " Its threshold was not changed.");
            }
        }
        else
        {
            lines.Add("Headset mic: skipped (not set or not connected).");
        }

        _loading = true;
        ThresholdSlider.Value = _settings.VoiceThresholdDb;
        HeadsetThresholdSlider.Value = _settings.HeadsetVoiceThresholdDb;
        UpdateSliderLabels();
        _loading = false;
        _persist();

        CalibrationText.Text = string.Join(Environment.NewLine, lines);
        AppLog.Info($"Calibrated: pro threshold {_settings.VoiceThresholdDb:0} dB, headset threshold {_settings.HeadsetVoiceThresholdDb:0} dB");
    }

    private static double Clamp(double value, System.Windows.Controls.Slider slider) =>
        Math.Clamp(Math.Round(value, 1), slider.Minimum, slider.Maximum);
}
