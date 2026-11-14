using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MicSwitcher.App.Audio;
using MicSwitcher.Core;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;

namespace MicSwitcher.App;

/// <summary>First-run (and re-runnable) guide: language, theme, choosing mics, startup, then optional calibration.</summary>
public sealed class SetupWizard : FluentWindow
{
    private const int LastPage = 3;

    private sealed class DeviceChoice
    {
        public required CaptureDevice Device { get; init; }
        public System.Windows.Controls.CheckBox Use { get; } = new();
        public System.Windows.Controls.CheckBox Fallback { get; } = new();
    }

    private readonly AppServices _s;
    private readonly AppSettings _settings;
    private readonly ContentControl _page = new();
    private readonly Wpf.Ui.Controls.Button _back = new();
    private readonly Wpf.Ui.Controls.Button _next = new() { Appearance = ControlAppearance.Primary, IsDefault = true };
    private readonly TextBlock _stepText = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly List<DeviceChoice> _choices = new();

    private System.Windows.Controls.CheckBox _startup = null!;
    private System.Windows.Controls.CheckBox _updates = null!;
    private System.Windows.Controls.CheckBox _notify = null!;
    private System.Windows.Controls.CheckBox _calibrate = null!;
    private int _index;
    private bool _devicesLoaded;

    public SetupWizard(AppServices services)
    {
        _s = services;
        _settings = services.Settings;

        Width = 600;
        Height = 640;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ExtendsContentIntoTitleBar = true;
        WindowBackdropType = AppTheme.IsBlack ? WindowBackdropType.None : WindowBackdropType.Mica;
        if (AppTheme.IsBlack)
        {
            Background = Brushes.Black;
        }

        _back.Click += (_, _) => Go(_index - 1);
        _next.Click += (_, _) =>
        {
            if (_index == LastPage)
            {
                Finish();
            }
            else
            {
                Go(_index + 1);
            }
        };

        Loaded += (_, _) => AppTheme.Apply(_settings.Theme, this);
        Go(0);
    }

    /// <summary>True when the user asked to calibrate right after the setup.</summary>
    public bool CalibrateWhenDone { get; private set; }

    private void Go(int index)
    {
        _index = Math.Clamp(index, 0, LastPage);
        FlowDirection = Loc.Direction;
        Title = Loc.T("Setup_Title");

        var body = new StackPanel();
        switch (_index)
        {
            case 0: BuildWelcome(body); break;
            case 1: BuildMics(body); break;
            case 2: BuildBehaviour(body); break;
            default: BuildFinish(body); break;
        }

        _back.Content = Loc.T("Setup_Back");
        _back.IsEnabled = _index > 0;
        _next.Content = Loc.T(_index == LastPage ? "Setup_Finish" : "Setup_Next");
        _stepText.Text = Loc.F("Setup_Step", _index + 1, LastPage + 1);

        var footer = new DockPanel { Margin = new Thickness(24, 8, 24, 20), LastChildFill = false };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        _back.Margin = new Thickness(0, 0, 8, 0);
        buttons.Children.Add(_back);
        buttons.Children.Add(_next);
        DockPanel.SetDock(buttons, Dock.Right);
        footer.Children.Add(buttons);
        footer.Children.Add(_stepText);
        DetachFooter();

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = body, Padding = new Thickness(24, 0, 24, 0) };
        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition());
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.Children.Add(new TitleBar { Title = Title });
        Grid.SetRow(scroll, 1);
        layout.Children.Add(scroll);
        Grid.SetRow(footer, 2);
        layout.Children.Add(footer);
        Content = layout;
    }

    private void DetachFooter()
    {
        foreach (var element in new FrameworkElement[] { _back, _next, _stepText })
        {
            if (element.Parent is Panel panel)
            {
                panel.Children.Remove(element);
            }
        }
    }

    private static TextBlock Heading(string text) =>
        new() { Text = text, FontSize = 24, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 8), TextWrapping = TextWrapping.Wrap };

    private static TextBlock Body(string text, double top = 0)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, top, 0, 8) };
        block.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        return block;
    }

    private static TextBlock Label(string text) =>
        new() { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 16, 0, 6) };

    private void BuildWelcome(StackPanel body)
    {
        body.Children.Add(Heading(Loc.T("Setup_WelcomeTitle")));
        body.Children.Add(Body(Loc.T("Setup_WelcomeText")));

        body.Children.Add(Label(Loc.T("Appearance_Language")));
        var language = new System.Windows.Controls.ComboBox();
        language.Items.Add(new ComboBoxItem { Content = Loc.T("Language_Auto"), Tag = "auto" });
        foreach (AppLanguage lang in Loc.Languages)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            try
            {
                row.Children.Add(new System.Windows.Controls.Image
                {
                    Source = new BitmapImage(new Uri("pack://application:,,,/" + lang.FlagResource)),
                    Width = 24,
                    Height = 16,
                    Margin = new Thickness(0, 0, 10, 0),
                });
            }
            catch (Exception)
            {
                // The flag is decoration only.
            }

            row.Children.Add(new TextBlock { Text = lang.NativeName });
            language.Items.Add(new ComboBoxItem { Content = row, Tag = lang.Code });
        }

        SelectTag(language, _settings.Language);
        language.SelectionChanged += (_, _) =>
        {
            if (language.SelectedItem is ComboBoxItem item && (string)item.Tag != _settings.Language)
            {
                _settings.Language = (string)item.Tag;
                Loc.Apply(_settings.Language);
                Go(0);
            }
        };
        body.Children.Add(language);

        body.Children.Add(Label(Loc.T("Appearance_Theme")));
        var theme = new System.Windows.Controls.ComboBox();
        foreach (string name in new[] { "System", "Light", "Dark", "Black" })
        {
            theme.Items.Add(new ComboBoxItem { Content = Loc.T("Theme_" + name), Tag = name });
        }

        SelectTag(theme, AppTheme.Normalize(_settings.Theme));
        theme.SelectionChanged += (_, _) =>
        {
            if (theme.SelectedItem is ComboBoxItem item)
            {
                _settings.Theme = (string)item.Tag;
                AppTheme.Apply(_settings.Theme, this);
            }
        };
        body.Children.Add(theme);
    }

    private static void SelectTag(System.Windows.Controls.ComboBox combo, string tag)
    {
        foreach (ComboBoxItem item in combo.Items)
        {
            if ((string)item.Tag == tag)
            {
                combo.SelectedItem = item;
                return;
            }
        }

        combo.SelectedIndex = 0;
    }

    private void LoadDevices()
    {
        if (_devicesLoaded)
        {
            return;
        }

        _devicesLoaded = true;
        IReadOnlyList<CaptureDevice> devices;
        try
        {
            devices = _s.Devices.GetActiveCaptureDevices();
        }
        catch (Exception ex)
        {
            AppLog.Info($"Could not list the microphones: {ex.Message}");
            devices = Array.Empty<CaptureDevice>();
        }

        bool rerun = _settings.Mics.Any(m => m.DeviceId is not null);
        int preselected = 0;
        foreach (CaptureDevice device in devices)
        {
            var choice = new DeviceChoice { Device = device };
            MicSlot? existing = _settings.Mics.FirstOrDefault(m => m.DeviceId == device.Id);
            bool builtIn = Regex.IsMatch(device.Name, "array|built-?in|internal|laptop|integrated", RegexOptions.IgnoreCase);

            if (rerun)
            {
                choice.Use.IsChecked = existing is not null;
                choice.Fallback.IsChecked = existing?.Fallback ?? false;
            }
            else
            {
                choice.Use.IsChecked = preselected < 3;
                choice.Fallback.IsChecked = builtIn;
                if (preselected < 3)
                {
                    preselected++;
                }
            }

            _choices.Add(choice);
        }
    }

    private void BuildMics(StackPanel body)
    {
        LoadDevices();
        body.Children.Add(Heading(Loc.T("Setup_MicsTitle")));
        body.Children.Add(Body(Loc.T("Setup_MicsText")));

        if (_choices.Count == 0)
        {
            body.Children.Add(Body(Loc.T("Setup_NoDevices"), 12));
            return;
        }

        foreach (DeviceChoice choice in _choices)
        {
            choice.Use.Content = choice.Device.Name;
            choice.Fallback.Content = Loc.T("Setup_IsFallback");
            choice.Fallback.FontSize = 12;
            choice.Fallback.Margin = new Thickness(28, 0, 0, 0);

            // The same control instances are reused when the page is rebuilt, so detach them first.
            (choice.Use.Parent as Panel)?.Children.Remove(choice.Use);
            (choice.Fallback.Parent as Panel)?.Children.Remove(choice.Fallback);

            var card = new Border { Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 0, 8), CornerRadius = new CornerRadius(6) };
            card.SetResourceReference(Border.BackgroundProperty, "ControlFillColorDefaultBrush");
            var stack = new StackPanel();
            stack.Children.Add(choice.Use);
            stack.Children.Add(choice.Fallback);
            card.Child = stack;
            body.Children.Add(card);
        }

        body.Children.Add(Body(Loc.T("Setup_OrderHint"), 4));
    }

    private void BuildBehaviour(StackPanel body)
    {
        body.Children.Add(Heading(Loc.T("Setup_BehaviourTitle")));

        _startup = Check(Loc.T("Setup_Startup"), _settings.StartWithWindows || !_settings.FirstRunCompleted);
        _updates = Check(Loc.T("Setup_Updates"), _settings.AutoCheckUpdates);
        _notify = Check(Loc.T("Setup_Notify"), _settings.NotifyOnSwitch);
        body.Children.Add(_startup);
        body.Children.Add(_updates);
        body.Children.Add(_notify);
    }

    private static System.Windows.Controls.CheckBox Check(string text, bool value) =>
        new() { Content = text, IsChecked = value, Margin = new Thickness(0, 6, 0, 0) };

    private void BuildFinish(StackPanel body)
    {
        body.Children.Add(Heading(Loc.T("Setup_DoneTitle")));
        body.Children.Add(Body(Loc.T("Setup_DoneText")));

        int count = _choices.Count(c => c.Use.IsChecked == true);
        body.Children.Add(Body(Loc.F("Setup_DoneCount", count), 4));

        _calibrate = Check(Loc.T("Setup_CalibrateNow"), count > 0);
        _calibrate.IsEnabled = count > 0;
        body.Children.Add(_calibrate);
        body.Children.Add(Body(Loc.T("Setup_CalibrateHint")));
    }

    private void Finish()
    {
        // Keep the settings of mics that stay, add new ones, drop unchecked ones and empty placeholders.
        var result = new List<MicSlot>();
        foreach (MicSlot slot in _settings.Mics)
        {
            DeviceChoice? choice = _choices.FirstOrDefault(c => c.Device.Id == slot.DeviceId);
            if (choice is null)
            {
                if (_choices.Count == 0 && slot.DeviceId is not null)
                {
                    result.Add(slot); // no devices were listed: do not throw the user's setup away
                }

                continue;
            }

            if (choice.Use.IsChecked == true)
            {
                slot.Fallback = choice.Fallback.IsChecked == true;
                result.Add(slot);
            }
        }

        foreach (DeviceChoice choice in _choices.Where(c => c.Use.IsChecked == true))
        {
            if (result.All(m => m.DeviceId != choice.Device.Id))
            {
                result.Add(new MicSlot
                {
                    DeviceId = choice.Device.Id,
                    DeviceName = choice.Device.Name,
                    Fallback = choice.Fallback.IsChecked == true,
                });
            }
        }

        // Normal mics first (in the order chosen), fallback mics last.
        _settings.Mics = result.Where(m => !m.Fallback).Concat(result.Where(m => m.Fallback)).Take(10).ToList();

        _settings.AutoCheckUpdates = _updates.IsChecked == true;
        _settings.NotifyOnSwitch = _notify.IsChecked == true;
        bool startup = _startup.IsChecked == true;
        if (startup != _settings.StartWithWindows)
        {
            string? error = StartupManager.SetEnabled(startup);
            if (error is null)
            {
                _settings.StartWithWindows = startup;
            }
            else
            {
                AppLog.Info($"Could not change the startup setting: {error}");
            }
        }

        _settings.FirstRunCompleted = true;
        CalibrateWhenDone = _calibrate.IsChecked == true && _settings.Mics.Any(m => !m.Fallback);
        _s.Persist();
        Close();
    }
}
