using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MicSwitcher.App.Audio;
using MicSwitcher.Core;

namespace MicSwitcher.App;

/// <summary>Settings window: mics, live meters, switching options, history, calibration, shortcuts and more.</summary>
public partial class MainWindow : Wpf.Ui.Controls.FluentWindow
{
    private const int MaxMics = 10;
    private static readonly Brush NoneBrush = new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80));

    private readonly AppServices _s;
    private readonly AppSettings _settings;
    private readonly SwitchController _controller;
    private readonly AudioDeviceService _devices;
    private readonly Dictionary<string, MicRow> _rows = new();
    private readonly ObservableCollection<string> _logLines = new();
    private readonly DispatcherTimer _deviceTimer;
    private readonly object _calibrationLock = new();

    // True while the UI is being filled from settings, so change handlers do not write back.
    private bool _loading = true;
    private volatile bool _visible;
    private string _deviceSignature = string.Empty;
    private IReadOnlyList<CaptureDevice> _inputDevices = Array.Empty<CaptureDevice>();
    private Dictionary<string, List<double>>? _activeSinks;
    private CancellationTokenSource? _calibrationCts;

    public MainWindow(AppServices services)
    {
        _s = services;
        _settings = services.Settings;
        _controller = services.Controller;
        _devices = services.Devices;

        FlowDirection = Loc.Direction;
        InitializeComponent();

        LogList.ItemsSource = _logLines;
        foreach (string line in AppLog.Snapshot())
        {
            _logLines.Add(line);
        }

        BuildLanguageCombo();
        RebuildMicList();

        AppLog.LineAdded += OnLogLine;
        _controller.LevelMeasured += OnLevelMeasured;
        _controller.History.Added += OnHistoryAdded;

        _deviceTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _deviceTimer.Tick += (_, _) =>
        {
            RefreshDeviceLists();
            RefreshTimeline();
        };

        IsVisibleChanged += OnVisibleChanged;
        Loaded += (_, _) => AppTheme.Apply(_settings.Theme, this);

        LoadSettingsIntoControls();
        RefreshDeviceLists();
        if (_controller.LastStatus is ControllerStatus status)
        {
            UpdateStatus(status);
        }
        else
        {
            ActiveText.Text = Loc.T("Status_Starting");
        }

        RefreshTimeline();
        _loading = false;
    }

    /// <summary>
    /// Closing really closes (and frees) the window; the app keeps running in the tray and builds a fresh window
    /// next time. This also means no stale window can survive sleep/wake.
    /// </summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        _calibrationCts?.Cancel();
        _deviceTimer.Stop();
        AppLog.LineAdded -= OnLogLine;
        _controller.LevelMeasured -= OnLevelMeasured;
        _controller.History.Added -= OnHistoryAdded;
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _visible = false;
        _controller.SetMetersWanted(false);
        base.OnClosed(e);
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
            RefreshTimeline();
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

    /// <summary>Re-reads everything from the settings (after the profile or mics changed elsewhere).</summary>
    public void RefreshAll()
    {
        bool wasLoading = _loading;
        _loading = true;
        try
        {
            RebuildMicList();
            LoadSettingsIntoControls();
            RefreshDeviceLists();
            RefreshTimeline();
            if (_controller.LastStatus is ControllerStatus status)
            {
                UpdateStatus(status);
            }
        }
        finally
        {
            _loading = wasLoading;
        }
    }

    private void LoadSettingsIntoControls()
    {
        bool wasLoading = _loading;
        _loading = true;
        try
        {
            Profile profile = _settings.ActiveProfile;

            PauseCheck.IsChecked = _settings.Paused;
            AdjustGainCheck.IsChecked = _settings.AdjustGainOnCalibrate;
            CommsCheck.IsChecked = _settings.SwitchCommunicationsRole;
            StartupCheck.IsChecked = _settings.StartWithWindows;
            NotifyCheck.IsChecked = _settings.NotifyOnSwitch;
            BleedCheck.IsChecked = profile.IgnoreBleed;
            SmartCheck.IsChecked = profile.SmartVoice;
            VoiceCheck.IsChecked = _settings.VoiceCommandsEnabled;
            WakeBox.Text = _settings.WakePhrase;

            ProfileCombo.ItemsSource = null;
            ProfileCombo.ItemsSource = _settings.Profiles;
            ProfileCombo.SelectedItem = profile;

            SelectByTag(ModeCombo, profile.Mode.ToString());
            SelectByTag(ThemeCombo, AppTheme.Normalize(_settings.Theme));
            SelectByTag(LanguageCombo, _settings.Language);

            AutoCheckCheck.IsChecked = _settings.AutoCheckUpdates;
            AutoInstallCheck.IsChecked = _settings.AutoInstallUpdates;
            PrereleaseCheck.IsChecked = _settings.IncludePrereleases;
            UpdateInfoText.Text = Loc.F("Updates_Version", UpdateService.CurrentVersion);

            SilenceSlider.Value = profile.SilenceTimeoutSeconds;
            ReturnSlider.Value = profile.ReturnDelayMs;
            UpdateSliderLabels();
            UpdateModeHint();
            UpdateHotkeySummary();
        }
        finally
        {
            _loading = wasLoading;
        }
    }

    private static void SelectByTag(ComboBox combo, string tag)
    {
        foreach (ComboBoxItem item in combo.Items)
        {
            if ((string)item.Tag == tag)
            {
                combo.SelectedItem = item;
                return;
            }
        }
    }

    private void UpdateSliderLabels()
    {
        if (SilenceValue is null || ReturnValue is null)
        {
            return; // raised while the XAML is still being built
        }

        SilenceValue.Text = $"{SilenceSlider.Value:0.0} s";
        ReturnValue.Text = $"{ReturnSlider.Value:0} ms";
    }

    private void UpdateModeHint()
    {
        ModeHint.Text = Loc.T("ModeHint_" + _settings.ActiveProfile.Mode);
        SilenceSlider.IsEnabled = _settings.ActiveProfile.Mode != SwitchMode.Manual;
        ReturnSlider.IsEnabled = _settings.ActiveProfile.Mode != SwitchMode.Manual;
    }

    private void UpdateHotkeySummary() =>
        HotkeySummary.Text = _settings.Hotkeys.Count == 0
            ? Loc.T("Hotkeys_None")
            : Loc.F("Hotkeys_Count", _settings.Hotkeys.Count) + "  " +
              string.Join("  ·  ", _settings.Hotkeys.Take(4).Select(h => h.Gesture));

    // ----- microphone list ------------------------------------------------------------------------------------

    private void RebuildMicList()
    {
        MicList.Children.Clear();
        _rows.Clear();

        for (int i = 0; i < _settings.Mics.Count; i++)
        {
            MicSlot slot = _settings.Mics[i];
            string id = slot.Id;
            var row = new MicRow();
            _rows[id] = row;

            row.Combo.SelectionChanged += (_, _) => OnDeviceSelected(id);
            row.EditRequested += (_, _) => CustomizeMic(id);
            row.MoveUpRequested += (_, _) => MoveMic(id, -1);
            row.MoveDownRequested += (_, _) => MoveMic(id, +1);
            row.RemoveRequested += (_, _) => RemoveMic(id);
            row.ThresholdChanged += db =>
            {
                if (_loading)
                {
                    return;
                }

                MicSlot? target = _settings.FindMic(id);
                if (target is not null)
                {
                    target.VoiceThresholdDb = Math.Round(db, 1);
                    _s.Persist();
                }
            };

            ApplyRowAppearance(i);
            row.SetThresholdValue(slot.VoiceThresholdDb);
            row.SetMoveEnabled(i > 0, i < _settings.Mics.Count - 1);

            MicList.Children.Add(new Wpf.Ui.Controls.Card { Margin = new Thickness(0, 0, 0, 10), Padding = new Thickness(20), Content = row });
        }

        NoMicsText.Visibility = _settings.Mics.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        AddMicButton.IsEnabled = _settings.Mics.Count < MaxMics;
        _deviceSignature = string.Empty;
        if (_inputDevices.Count > 0)
        {
            PopulateCombos(_inputDevices);
        }
    }

    private void ApplyRowAppearance(int index)
    {
        MicSlot slot = _settings.Mics[index];
        if (!_rows.TryGetValue(slot.Id, out MicRow? row))
        {
            return;
        }

        row.SetAppearance(slot.DisplayName(index), MicAppearance.SymbolOf(slot), MicAppearance.ImageOf(slot));
        row.SetSubtitle(slot.Fallback
            ? Loc.T("Row_FallbackHint")
            : index == 0 ? Loc.T("Row_MainHint") : Loc.F("Row_PriorityHint", index + 1));
    }

    private void RefreshDeviceLists()
    {
        IReadOnlyList<CaptureDevice> devices;
        try
        {
            devices = _devices.GetActiveCaptureDevices();
        }
        catch (Exception ex)
        {
            AppLog.Info($"Could not list the microphones: {ex.Message}");
            return;
        }

        string signature = string.Join("|", devices.Select(d => d.Id)) + "#" + string.Join("|", _settings.Mics.Select(m => m.Id + m.DeviceId));
        if (signature == _deviceSignature)
        {
            return;
        }

        _deviceSignature = signature;
        _inputDevices = devices;

        bool wasLoading = _loading;
        _loading = true;
        try
        {
            PopulateCombos(devices);
        }
        finally
        {
            _loading = wasLoading;
        }
    }

    private void PopulateCombos(IReadOnlyList<CaptureDevice> devices)
    {
        foreach (MicSlot slot in _settings.Mics)
        {
            if (!_rows.TryGetValue(slot.Id, out MicRow? row))
            {
                continue;
            }

            var items = new List<DeviceItem> { new(null, Loc.T("Row_NotSet")) };
            items.AddRange(devices.Select(d => new DeviceItem(d.Id, d.Name)));
            if (slot.DeviceId is not null && devices.All(d => d.Id != slot.DeviceId))
            {
                items.Add(new DeviceItem(slot.DeviceId, (slot.DeviceName ?? Loc.T("Row_UnknownDevice")) + Loc.T("Row_MissingSuffix")));
            }

            bool wasLoading = _loading;
            _loading = true;
            row.Combo.ItemsSource = items;
            row.Combo.SelectedItem = items.FirstOrDefault(i => i.Id == slot.DeviceId) ?? items[0];
            _loading = wasLoading;
        }
    }

    private void OnDeviceSelected(string micId)
    {
        if (_loading || !_rows.TryGetValue(micId, out MicRow? row) || row.Combo.SelectedItem is not DeviceItem item)
        {
            return;
        }

        MicSlot? slot = _settings.FindMic(micId);
        if (slot is null || item.Id == slot.DeviceId)
        {
            return;
        }

        slot.DeviceId = item.Id;
        slot.DeviceName = item.Id is null ? null : item.Name;

        if (item.Id is not null && _settings.Mics.Any(m => m.Id != micId && m.DeviceId == item.Id))
        {
            AppLog.Info(Loc.F("Log_SameDevice", item.Name));
        }

        AppLog.Info(Loc.F("Log_DeviceSet", _settings.MicName(micId), item.Name));
        _deviceSignature = string.Empty;
        int index = _settings.IndexOfMic(micId);
        ApplyRowAppearance(index);
        _s.Persist();
    }

    private void AddMicButton_Click(object sender, RoutedEventArgs e)
    {
        if (_settings.Mics.Count >= MaxMics)
        {
            return;
        }

        _settings.Mics.Add(new MicSlot());
        _s.Persist();
        RebuildMicList();
    }

    private void MoveMic(string id, int delta)
    {
        int index = _settings.IndexOfMic(id);
        int target = index + delta;
        if (index < 0 || target < 0 || target >= _settings.Mics.Count)
        {
            return;
        }

        (_settings.Mics[index], _settings.Mics[target]) = (_settings.Mics[target], _settings.Mics[index]);
        _s.Persist();
        RebuildMicList();
    }

    private void RemoveMic(string id)
    {
        MicSlot? slot = _settings.FindMic(id);
        if (slot is null)
        {
            return;
        }

        MessageBoxResult answer = MessageBox.Show(
            this,
            Loc.F("Row_RemoveConfirm", _settings.MicName(id)),
            "MicSwitcher",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        _settings.Mics.Remove(slot);
        _settings.Hotkeys.RemoveAll(h => h.MicId == id);
        _settings.Normalize();
        _s.Persist();
        _s.ApplyHotkeys();
        RebuildMicList();
        UpdateHotkeySummary();
    }

    private void CustomizeMic(string id)
    {
        MicSlot? slot = _settings.FindMic(id);
        if (slot is null)
        {
            return;
        }

        IReadOnlyList<CaptureDevice> outputs;
        try
        {
            outputs = _devices.GetActiveRenderDevices();
        }
        catch (Exception)
        {
            outputs = Array.Empty<CaptureDevice>();
        }

        var dialog = new MicCustomizeDialog(slot, outputs) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        slot.Label = dialog.ResultLabel;
        slot.IconKey = dialog.ResultIconKey;
        slot.IconPath = dialog.ResultIconPath;
        slot.Fallback = dialog.ResultFallback;
        slot.OutputDeviceId = dialog.ResultOutputId;
        slot.OutputDeviceName = dialog.ResultOutputName;
        slot.OutputVolumePercent = dialog.ResultVolume;
        _s.Persist();
        RebuildMicList();
        if (_controller.LastStatus is ControllerStatus status)
        {
            UpdateStatus(status);
        }
    }

    // ----- live data from the controller ----------------------------------------------------------------------

    private void OnLevelMeasured(string micId, double db)
    {
        lock (_calibrationLock)
        {
            if (_activeSinks is not null && _activeSinks.TryGetValue(micId, out List<double>? sink))
            {
                sink.Add(db);
            }
        }

        if (!_visible || Dispatcher.HasShutdownStarted)
        {
            return;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (_rows.TryGetValue(micId, out MicRow? row))
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

    private void OnHistoryAdded(SwitchEvent item)
    {
        if (Dispatcher.HasShutdownStarted)
        {
            return;
        }

        Dispatcher.BeginInvoke(new Action(RefreshTimeline));
    }

    public void UpdateStatus(ControllerStatus status)
    {
        int activeIndex = status.ActiveMicId is null ? -1 : _settings.IndexOfMic(status.ActiveMicId);
        ActiveText.Text = activeIndex < 0 ? Loc.T("Status_NoneOfYourMics") : _settings.MicName(status.ActiveMicId!);
        StatusDot.Fill = status.Paused || activeIndex < 0 ? NoneBrush : MicColors.BrushAt(activeIndex);

        DetailText.Text = _settings.Mics.All(m => m.DeviceId is null) ? Loc.T("Status_ChooseMics") : status.Message;
        ReleaseLockButton.Visibility = status.LockedMicId is null ? Visibility.Collapsed : Visibility.Visible;

        for (int i = 0; i < _settings.Mics.Count; i++)
        {
            MicSlot slot = _settings.Mics[i];
            if (!_rows.TryGetValue(slot.Id, out MicRow? row))
            {
                continue;
            }

            MicStatus? state = status.Find(slot.Id);
            if (slot.DeviceId is null)
            {
                row.SetBadge(string.Empty, BadgeKind.Muted);
            }
            else if (state?.Present != true)
            {
                row.SetBadge(Loc.T("Badge_NotFound"), BadgeKind.Problem);
            }
            else
            {
                string prefix = status.ActiveMicId == slot.Id ? Loc.T("Badge_Default") + " · " : string.Empty;
                bool voice = state.Voice;
                row.SetBadge(prefix + Loc.T(voice ? "Badge_Voice" : slot.Fallback ? "Badge_Connected" : "Badge_Quiet"),
                    voice ? BadgeKind.Good : BadgeKind.Muted);
            }
        }

        SyncPaused();
    }

    public void SyncPaused()
    {
        bool wasLoading = _loading;
        _loading = true;
        PauseCheck.IsChecked = _settings.Paused;
        _loading = wasLoading;
    }

    // ----- timeline -------------------------------------------------------------------------------------------

    private void RefreshTimeline()
    {
        DateTime to = DateTime.UtcNow;
        DateTime from = to - TimeSpan.FromHours(6);
        IReadOnlyList<HistorySegment> segments = _controller.History.Segments(from, to);

        TimelineHost.ColumnDefinitions.Clear();
        TimelineHost.Children.Clear();
        TimelineLegend.Children.Clear();

        var listed = new HashSet<string>();
        int column = 0;

        void AddSegment(double seconds, Brush brush, string tip)
        {
            TimelineHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(1, seconds), GridUnitType.Star) });
            var border = new Border { Background = brush, ToolTip = tip, Margin = new Thickness(column == 0 ? 0 : 1, 0, 0, 0), CornerRadius = new CornerRadius(3) };
            Grid.SetColumn(border, column++);
            TimelineHost.Children.Add(border);
        }

        if (segments.Count > 0 && segments[0].Start > from)
        {
            AddSegment((segments[0].Start - from).TotalSeconds, NoneBrush, Loc.T("History_Unknown"));
        }

        foreach (HistorySegment segment in segments)
        {
            int index = _settings.IndexOfMic(segment.MicId);
            Brush brush = index < 0 ? NoneBrush : MicColors.BrushAt(index);
            AddSegment(segment.Length.TotalSeconds, brush, $"{segment.MicName} — {FormatSpan(segment.Length)}");

            if (index >= 0 && listed.Add(segment.MicId))
            {
                var legend = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 16, 4) };
                legend.Children.Add(new System.Windows.Shapes.Ellipse { Width = 10, Height = 10, Fill = brush, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
                legend.Children.Add(new TextBlock { Text = _settings.MicName(segment.MicId), FontSize = 12 });
                TimelineLegend.Children.Add(legend);
            }
        }

        TimelineHost.Visibility = segments.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        HistoryEmpty.Visibility = segments.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        HistoryList.ItemsSource = _controller.History.Snapshot()
            .Reverse()
            .Take(12)
            .Select(e => $"{e.Time.ToLocalTime():HH:mm:ss}   {e.ToName} — {e.Reason}  ({Loc.T("Source_" + e.Source)})")
            .ToList();
    }

    private static string FormatSpan(TimeSpan span) =>
        span.TotalHours >= 1 ? $"{(int)span.TotalHours} h {span.Minutes} min"
        : span.TotalMinutes >= 1 ? $"{(int)span.TotalMinutes} min"
        : $"{(int)span.TotalSeconds} s";

    // ----- option handlers ------------------------------------------------------------------------------------

    private void PauseCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loading)
        {
            _s.SetPaused(PauseCheck.IsChecked == true);
        }
    }

    private void ReleaseLockButton_Click(object sender, RoutedEventArgs e) => _controller.ReleaseLock();

    private void AdjustGainCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _settings.AdjustGainOnCalibrate = AdjustGainCheck.IsChecked == true;
        _s.Persist();
    }

    private void CommsCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _settings.SwitchCommunicationsRole = CommsCheck.IsChecked == true;
        _s.Persist();
    }

    private void NotifyCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _settings.NotifyOnSwitch = NotifyCheck.IsChecked == true;
        _s.Persist();
    }

    private void BleedCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _settings.ActiveProfile.IgnoreBleed = BleedCheck.IsChecked == true;
        _s.Persist();
    }

    private void SmartCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _settings.ActiveProfile.SmartVoice = SmartCheck.IsChecked == true;
        _s.Persist();
    }

    private void ProfileCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ProfileCombo.SelectedItem is not Profile profile)
        {
            return;
        }

        _s.SwitchProfile(profile.Id);
    }

    private void ManageProfilesButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ProfilesDialog(_settings) { Owner = this };
        dialog.ShowDialog();
        _settings.Normalize();
        _s.Persist();
        RefreshAll();
    }

    private void ModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ModeCombo.SelectedItem is not ComboBoxItem item)
        {
            return;
        }

        _settings.ActiveProfile.Mode = Enum.Parse<SwitchMode>((string)item.Tag);
        UpdateModeHint();
        _s.Persist();
    }

    private void SilenceSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsInitialized)
        {
            return;
        }

        UpdateSliderLabels();
        if (_loading)
        {
            return;
        }

        _settings.ActiveProfile.SilenceTimeoutSeconds = Math.Round(SilenceSlider.Value, 1);
        _s.Persist();
    }

    private void ReturnSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsInitialized)
        {
            return;
        }

        UpdateSliderLabels();
        if (_loading)
        {
            return;
        }

        _settings.ActiveProfile.ReturnDelayMs = Math.Round(ReturnSlider.Value);
        _s.Persist();
    }

    private void ThemeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ThemeCombo.SelectedItem is not ComboBoxItem item)
        {
            return;
        }

        _settings.Theme = (string)item.Tag;
        AppTheme.Apply(_settings.Theme, this);
        _s.Persist();
    }

    private void BuildLanguageCombo()
    {
        LanguageCombo.Items.Clear();
        LanguageCombo.Items.Add(new ComboBoxItem { Content = Loc.T("Language_Auto"), Tag = "auto" });
        foreach (AppLanguage language in Loc.Languages)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            try
            {
                panel.Children.Add(new Image
                {
                    Source = new BitmapImage(new Uri("pack://application:,,,/" + language.FlagResource)),
                    Width = 24,
                    Height = 16,
                    Margin = new Thickness(0, 0, 10, 0),
                });
            }
            catch (Exception ex)
            {
                AppLog.Info($"Could not load the flag for {language.Code}: {ex.Message}");
            }

            panel.Children.Add(new TextBlock { Text = language.NativeName, VerticalAlignment = VerticalAlignment.Center });
            LanguageCombo.Items.Add(new ComboBoxItem { Content = panel, Tag = language.Code });
        }
    }

    private void LanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || LanguageCombo.SelectedItem is not ComboBoxItem item)
        {
            return;
        }

        string code = (string)item.Tag;
        if (code == _settings.Language)
        {
            return;
        }

        _settings.Language = code;
        _s.Persist();
        _s.LanguageChanged(); // rebuilds this window in the new language
    }

    private void HotkeysButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new HotkeysDialog(_settings) { Owner = this };
        dialog.ShowDialog();
        _s.Persist();
        IReadOnlyList<string> failed = _s.ApplyHotkeys();
        UpdateHotkeySummary();
        if (failed.Count > 0)
        {
            MessageBox.Show(this, Loc.F("Hotkeys_Failed", string.Join(", ", failed)), "MicSwitcher", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void VoiceCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _settings.VoiceCommandsEnabled = VoiceCheck.IsChecked == true;
        _s.Persist();
        ApplyVoice();
    }

    private void WakeBox_LostFocus(object sender, RoutedEventArgs e)
    {
        string text = WakeBox.Text.Trim();
        if (_loading || text.Length == 0 || text == _settings.WakePhrase)
        {
            WakeBox.Text = _settings.WakePhrase;
            return;
        }

        _settings.WakePhrase = text;
        _s.Persist();
        ApplyVoice();
    }

    private void ApplyVoice()
    {
        string? error = _s.ApplyVoiceCommands();
        VoiceStatus.Text = !_settings.VoiceCommandsEnabled ? string.Empty
            : error is null ? Loc.F("Voice_Listening", _settings.WakePhrase)
            : Loc.F("Voice_Error", error);
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
            _s.Persist();
            AppLog.Info(Loc.T(want ? "Log_StartupOn" : "Log_StartupOff"));
            return;
        }

        AppLog.Info($"Could not change the startup setting: {error}");
        MessageBox.Show(this, error, "MicSwitcher", MessageBoxButton.OK, MessageBoxImage.Warning);

        _loading = true;
        StartupCheck.IsChecked = !want;
        _loading = false;
    }

    private void Updates_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _settings.AutoCheckUpdates = AutoCheckCheck.IsChecked == true;
        _settings.AutoInstallUpdates = AutoInstallCheck.IsChecked == true;
        _settings.IncludePrereleases = PrereleaseCheck.IsChecked == true;
        _s.Persist();
    }

    private async void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        UpdateButton.IsEnabled = false;
        try
        {
            UpdateInfoText.Text = Loc.T("Updates_Checking");
            ReleaseInfo? release = await _s.Updates.CheckAsync();

            if (release is null)
            {
                UpdateInfoText.Text = Loc.F("Updates_UpToDate", UpdateService.CurrentVersion);
                return;
            }

            UpdateInfoText.Text = Loc.F("Updates_Downloading", release.Tag);
            bool started = await _s.Updates.DownloadAndInstallAsync(release);
            UpdateInfoText.Text = Loc.T(started ? "Updates_Installing" : "Updates_Failed");
            if (started)
            {
                Application.Current.Shutdown();
            }
        }
        catch (Exception ex)
        {
            UpdateInfoText.Text = Loc.F("Updates_CheckFailed", ex.Message);
        }
        finally
        {
            UpdateButton.IsEnabled = true;
        }
    }

    // ----- help -----------------------------------------------------------------------------------------------

    private void SetupButton_Click(object sender, RoutedEventArgs e) => _s.RerunSetup();

    private string BuildReport() => Diagnostics.Build(_settings, _devices, _controller);

    private void CopyDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(BuildReport());
            HelpStatus.Text = Loc.T("Help_Copied");
        }
        catch (Exception ex)
        {
            HelpStatus.Text = ex.Message;
        }
    }

    private void SaveDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"MicSwitcher-diagnostics-{DateTime.Now:yyyyMMdd-HHmm}.txt",
            Filter = "Text (*.txt)|*.txt",
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            System.IO.File.WriteAllText(dialog.FileName, BuildReport());
            HelpStatus.Text = Loc.F("Help_Saved", dialog.FileName);
        }
        catch (Exception ex)
        {
            HelpStatus.Text = ex.Message;
        }
    }

    // ----- guided calibration ---------------------------------------------------------------------------------

    private void CancelCalibrationButton_Click(object sender, RoutedEventArgs e) => _calibrationCts?.Cancel();

    private async void CalibrateButton_Click(object sender, RoutedEventArgs e) => await RunCalibrationAsync();

    /// <summary>Starts the guided calibration (used by the setup wizard's "calibrate now").</summary>
    public void StartCalibration() => _ = Dispatcher.BeginInvoke(new Func<Task>(RunCalibrationAsync), DispatcherPriority.Loaded);

    private async Task RunCalibrationAsync()
    {
        if (_calibrationCts is not null)
        {
            return;
        }

        ControllerStatus? status = _controller.LastStatus;
        List<MicSlot> mics = _settings.Mics
            .Where(m => !m.Fallback && m.DeviceId is not null && status?.Find(m.Id)?.Present == true)
            .ToList();

        if (mics.Count == 0)
        {
            MessageBox.Show(this, Loc.T("Calibration_NoMic"), "MicSwitcher", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        int totalSteps = 1 + mics.Count;
        var noise = mics.ToDictionary(m => m.Id, _ => new List<double>());
        var speech = mics.ToDictionary(m => m.Id, _ => new List<double>());
        var interference = mics.ToDictionary(m => m.Id, _ => new List<double>());

        CalibrateButton.IsEnabled = false;
        CalibrationText.Text = string.Empty;
        CalibrationPanel.Visibility = Visibility.Visible;
        _calibrationCts = new CancellationTokenSource();
        CancellationToken token = _calibrationCts.Token;

        try
        {
            await RunPhaseAsync(1, totalSteps, Loc.T("Calibration_QuietTitle"), Loc.T("Calibration_QuietHint"),
                leadInSeconds: 0.5, seconds: 3, token, noise);

            for (int j = 0; j < mics.Count; j++)
            {
                // While speaking into mic j, the mics above it in the list must not take that as their own voice.
                var sinks = new Dictionary<string, List<double>> { [mics[j].Id] = speech[mics[j].Id] };
                for (int i = 0; i < j; i++)
                {
                    sinks[mics[i].Id] = interference[mics[i].Id];
                }

                string name = _settings.MicName(mics[j].Id);
                await RunPhaseAsync(2 + j, totalSteps, Loc.F("Calibration_SpeakTitle", name), Loc.T("Calibration_SpeakHint"),
                    leadInSeconds: j == 0 ? 1.5 : 2, seconds: 5, token, sinks);
            }

            ApplyCalibration(mics, noise, speech, interference);
        }
        catch (OperationCanceledException)
        {
            CalibrationText.Text = Loc.T("Calibration_Cancelled");
        }
        finally
        {
            lock (_calibrationLock)
            {
                _activeSinks = null;
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
        Dictionary<string, List<double>> sinks)
    {
        CalibrationStepText.Text = Loc.F("Calibration_Step", step, totalSteps);
        CalibrationTitleText.Text = title;
        CalibrationHintText.Text = hint;
        CalibrationProgress.Value = 0;

        // Lead-in: gives the user a moment to react; nothing is recorded yet.
        var leadIn = Stopwatch.StartNew();
        CountdownText.FontSize = 40;
        while (leadIn.Elapsed.TotalSeconds < leadInSeconds)
        {
            CountdownText.Text = Loc.T("Calibration_GetReady");
            await Task.Delay(50, token);
        }

        CountdownText.FontSize = 64;
        lock (_calibrationLock)
        {
            _activeSinks = sinks;
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
            _activeSinks = null;
        }
    }

    private void ApplyCalibration(
        List<MicSlot> mics,
        Dictionary<string, List<double>> noise,
        Dictionary<string, List<double>> speech,
        Dictionary<string, List<double>> interference)
    {
        var lines = new List<string>();
        bool changedAnything = false;

        for (int k = 0; k < mics.Count; k++)
        {
            MicSlot mic = mics[k];
            string name = _settings.MicName(mic.Id);
            bool adjustGain = k == 0 && _settings.AdjustGainOnCalibrate;
            List<double> others = interference[mic.Id];

            CalibrationResult result = Calibration.Compute(
                noise[mic.Id], speech[mic.Id], _settings.TargetSpeechDb, adjustGain,
                interferenceLevelsDb: others.Count > 0 ? others : null);

            if (!result.Reliable)
            {
                lines.Add(Loc.F("Calibration_Unreliable", name, result.Warning ?? Loc.T("Calibration_Failed")));
                continue;
            }

            double appliedGain = 0;
            string gainNote = string.Empty;
            if (adjustGain && mic.DeviceId is not null && Math.Abs(result.GainDeltaDb) >= 0.5)
            {
                try
                {
                    appliedGain = _devices.AdjustGainDb(mic.DeviceId, result.GainDeltaDb);
                    gainNote = " " + Loc.F("Calibration_GainChanged", $"{appliedGain:+0.#;-0.#}");
                }
                catch (Exception ex)
                {
                    gainNote = " " + Loc.T("Calibration_GainFailed");
                    AppLog.Info($"Could not change the input volume: {ex.Message}");
                }
            }

            // The threshold assumed the full gain change; correct it by what was really applied.
            mic.VoiceThresholdDb = Math.Clamp(Math.Round(result.ThresholdDb - result.GainDeltaDb + appliedGain, 1), -80, -10);
            changedAnything = true;

            lines.Add(Loc.F("Calibration_Line", name, $"{result.NoiseFloorDb:0}", $"{result.SpeechLevelDb:0}", $"{mic.VoiceThresholdDb:0}", gainNote));
            if (result.Warning is not null)
            {
                lines.Add($"{name}: {result.Warning}");
            }
        }

        if (changedAnything)
        {
            _s.Persist();
            RebuildMicList();
        }

        CalibrationText.Text = string.Join(Environment.NewLine, lines);
        AppLog.Info(Loc.T("Log_Calibrated"));
    }
}
