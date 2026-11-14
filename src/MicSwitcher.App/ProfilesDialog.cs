using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using MicSwitcher.Core;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;

namespace MicSwitcher.App;

/// <summary>Add, rename, delete and tweak profiles (named sets of switching rules).</summary>
public sealed class ProfilesDialog : FluentWindow
{
    private readonly AppSettings _settings;
    private readonly System.Windows.Controls.ListBox _list = new() { Height = 150, DisplayMemberPath = nameof(Profile.Name) };
    private readonly System.Windows.Controls.TextBox _name = new() { MaxLength = 40 };
    private readonly System.Windows.Controls.ComboBox _mode = new();
    private readonly Slider _silence = new() { Minimum = 0.5, Maximum = 10, TickFrequency = 0.5, IsSnapToTickEnabled = true };
    private readonly Slider _return = new() { Minimum = 0, Maximum = 1000, TickFrequency = 50, IsSnapToTickEnabled = true };
    private readonly TextBlock _silenceText = new() { Width = 56, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _returnText = new() { Width = 56, VerticalAlignment = VerticalAlignment.Center };
    private readonly System.Windows.Controls.CheckBox _bleed = new();
    private readonly System.Windows.Controls.CheckBox _smart = new();
    private readonly System.Windows.Controls.ComboBox _forced = new();
    private readonly StackPanel _excluded = new();
    private readonly StackPanel _editor = new();
    private readonly Wpf.Ui.Controls.Button _delete = new() { Content = "" };

    private sealed record Choice(string? Id, string Name);

    private bool _loading;

    public ProfilesDialog(AppSettings settings)
    {
        _settings = settings;

        FlowDirection = Loc.Direction;
        Title = Loc.T("Profiles_Title");
        Width = 520;
        Height = 700;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ExtendsContentIntoTitleBar = true;
        WindowBackdropType = AppTheme.IsBlack ? WindowBackdropType.None : WindowBackdropType.Mica;
        if (AppTheme.IsBlack)
        {
            Background = System.Windows.Media.Brushes.Black;
        }

        var root = new StackPanel { Margin = new Thickness(24, 0, 24, 20) };
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = root };
        var panel = new Grid();
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition());
        panel.Children.Add(new TitleBar { Title = Title });
        Grid.SetRow(scroll, 1);
        panel.Children.Add(scroll);
        Content = panel;

        root.Children.Add(new TextBlock { Text = Loc.T("Profiles_Hint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });
        root.Children.Add(_list);

        var listButtons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        var add = new Wpf.Ui.Controls.Button { Content = Loc.T("Profiles_Add"), Margin = new Thickness(0, 0, 8, 0) };
        add.Click += (_, _) => AddProfile();
        _delete.Content = Loc.T("Profiles_Delete");
        _delete.Click += (_, _) => DeleteProfile();
        listButtons.Children.Add(add);
        listButtons.Children.Add(_delete);
        root.Children.Add(listButtons);

        // Editor for the selected profile.
        _editor.Margin = new Thickness(0, 14, 0, 0);
        root.Children.Add(_editor);

        _editor.Children.Add(Label(Loc.T("Profiles_Name")));
        _editor.Children.Add(_name);

        _editor.Children.Add(Label(Loc.T("Mode_Label")));
        foreach (SwitchMode mode in Enum.GetValues<SwitchMode>())
        {
            _mode.Items.Add(new ComboBoxItem { Content = Loc.T("Mode_" + mode), Tag = mode.ToString() });
        }

        _editor.Children.Add(_mode);

        _editor.Children.Add(Label(Loc.T("Switching_Silence")));
        _editor.Children.Add(SliderRow(_silence, _silenceText));
        _editor.Children.Add(Label(Loc.T("Switching_Return")));
        _editor.Children.Add(SliderRow(_return, _returnText));

        _bleed.Content = Loc.T("Switching_Bleed");
        _bleed.Margin = new Thickness(0, 12, 0, 0);
        _smart.Content = Loc.T("Switching_Smart");
        _smart.Margin = new Thickness(0, 4, 0, 0);
        _editor.Children.Add(_bleed);
        _editor.Children.Add(_smart);

        _editor.Children.Add(Label(Loc.T("Profiles_Forced")));
        _editor.Children.Add(_forced);
        _editor.Children.Add(Label(Loc.T("Profiles_Excluded")));
        _editor.Children.Add(_excluded);

        var done = new Wpf.Ui.Controls.Button
        {
            Content = Loc.T("Common_Done"),
            Appearance = ControlAppearance.Primary,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 20, 0, 0),
            IsDefault = true,
            IsCancel = true,
        };
        done.Click += (_, _) => Close();
        root.Children.Add(done);

        _list.ItemsSource = _settings.Profiles;
        _list.SelectedItem = _settings.ActiveProfile;
        _list.SelectionChanged += (_, _) => LoadEditor();

        _name.TextChanged += (_, _) => Apply(p => p.Name = string.IsNullOrWhiteSpace(_name.Text) ? p.Name : _name.Text.Trim(), refreshList: true);
        _mode.SelectionChanged += (_, _) => Apply(p => p.Mode = Enum.Parse<SwitchMode>((string)((ComboBoxItem)_mode.SelectedItem).Tag));
        _silence.ValueChanged += (_, _) => { _silenceText.Text = $"{_silence.Value:0.0} s"; Apply(p => p.SilenceTimeoutSeconds = _silence.Value); };
        _return.ValueChanged += (_, _) => { _returnText.Text = $"{_return.Value:0} ms"; Apply(p => p.ReturnDelayMs = _return.Value); };
        _bleed.Click += (_, _) => Apply(p => p.IgnoreBleed = _bleed.IsChecked == true);
        _smart.Click += (_, _) => Apply(p => p.SmartVoice = _smart.IsChecked == true);
        _forced.SelectionChanged += (_, _) => Apply(p => p.ForcedMicId = (_forced.SelectedItem as Choice)?.Id);

        LoadEditor();
    }

    private Profile? Selected => _list.SelectedItem as Profile;

    private static TextBlock Label(string text) =>
        new() { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 6) };

    private static DockPanel SliderRow(Slider slider, TextBlock value)
    {
        var row = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(value, Dock.Right);
        row.Children.Add(value);
        row.Children.Add(slider);
        return row;
    }

    private void Apply(Action<Profile> change, bool refreshList = false)
    {
        if (_loading || Selected is not Profile profile)
        {
            return;
        }

        change(profile);
        if (refreshList)
        {
            _loading = true;
            object? selected = _list.SelectedItem;
            _list.ItemsSource = null;
            _list.ItemsSource = _settings.Profiles;
            _list.SelectedItem = selected;
            _loading = false;
        }
    }

    private void LoadEditor()
    {
        if (Selected is not Profile profile)
        {
            _editor.IsEnabled = false;
            return;
        }

        _loading = true;
        _editor.IsEnabled = true;
        _delete.IsEnabled = _settings.Profiles.Count > 1;
        _name.Text = profile.Name;
        _mode.SelectedItem = _mode.Items.Cast<ComboBoxItem>().First(i => (string)i.Tag == profile.Mode.ToString());
        _silence.Value = profile.SilenceTimeoutSeconds;
        _return.Value = profile.ReturnDelayMs;
        _silenceText.Text = $"{_silence.Value:0.0} s";
        _returnText.Text = $"{_return.Value:0} ms";
        _bleed.IsChecked = profile.IgnoreBleed;
        _smart.IsChecked = profile.SmartVoice;

        var choices = new List<Choice> { new(null, Loc.T("Profiles_ForcedNone")) };
        for (int i = 0; i < _settings.Mics.Count; i++)
        {
            choices.Add(new Choice(_settings.Mics[i].Id, _settings.Mics[i].DisplayName(i)));
        }

        _forced.ItemsSource = choices;
        _forced.DisplayMemberPath = nameof(Choice.Name);
        _forced.SelectedItem = choices.FirstOrDefault(c => c.Id == profile.ForcedMicId) ?? choices[0];

        _excluded.Children.Clear();
        for (int i = 0; i < _settings.Mics.Count; i++)
        {
            string id = _settings.Mics[i].Id;
            var check = new System.Windows.Controls.CheckBox
            {
                Content = _settings.Mics[i].DisplayName(i),
                IsChecked = profile.ExcludedMicIds.Contains(id),
            };
            check.Click += (_, _) => Apply(p =>
            {
                p.ExcludedMicIds.Remove(id);
                if (check.IsChecked == true)
                {
                    p.ExcludedMicIds.Add(id);
                }
            });
            _excluded.Children.Add(check);
        }

        _loading = false;
    }

    private void AddProfile()
    {
        Profile source = Selected ?? _settings.ActiveProfile;
        var copy = new Profile
        {
            Name = Loc.F("Profiles_NewName", _settings.Profiles.Count + 1),
            Mode = source.Mode,
            SilenceTimeoutSeconds = source.SilenceTimeoutSeconds,
            ReturnDelayMs = source.ReturnDelayMs,
            MinSwitchIntervalSeconds = source.MinSwitchIntervalSeconds,
            IgnoreBleed = source.IgnoreBleed,
            SmartVoice = source.SmartVoice,
            ExcludedMicIds = new List<string>(source.ExcludedMicIds),
            ForcedMicId = source.ForcedMicId,
        };
        _settings.Profiles.Add(copy);
        _list.ItemsSource = null;
        _list.ItemsSource = _settings.Profiles;
        _list.SelectedItem = copy;
        _name.Focus();
        _name.SelectAll();
    }

    private void DeleteProfile()
    {
        if (Selected is not Profile profile || _settings.Profiles.Count <= 1)
        {
            return;
        }

        _settings.Profiles.Remove(profile);
        _settings.Hotkeys.RemoveAll(h => h.ProfileId == profile.Id);
        if (_settings.ActiveProfileId == profile.Id)
        {
            _settings.ActiveProfileId = _settings.Profiles[0].Id;
        }

        _list.ItemsSource = null;
        _list.ItemsSource = _settings.Profiles;
        _list.SelectedItem = _settings.ActiveProfile;
    }
}
