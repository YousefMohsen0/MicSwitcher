using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MicSwitcher.Core;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;

namespace MicSwitcher.App;

/// <summary>Edit the global keyboard shortcuts.</summary>
public sealed class HotkeysDialog : FluentWindow
{
    private sealed record Choice(string? Id, string Name);

    private readonly AppSettings _settings;
    private readonly StackPanel _rows = new();
    private readonly TextBlock _error = new() { Foreground = System.Windows.Media.Brushes.IndianRed, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };

    public HotkeysDialog(AppSettings settings)
    {
        _settings = settings;

        FlowDirection = Loc.Direction;
        Title = Loc.T("Hotkeys_Title");
        Width = 640;
        Height = 560;
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

        root.Children.Add(new TextBlock { Text = Loc.T("Hotkeys_Hint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        root.Children.Add(_rows);
        root.Children.Add(_error);

        var add = new Wpf.Ui.Controls.Button { Content = Loc.T("Hotkeys_Add"), Margin = new Thickness(0, 10, 0, 0) };
        add.Click += (_, _) =>
        {
            _settings.Hotkeys.Add(new HotkeyBinding { Action = HotkeyAction.TogglePause, Gesture = string.Empty });
            Rebuild();
        };
        root.Children.Add(add);

        var done = new Wpf.Ui.Controls.Button
        {
            Content = Loc.T("Common_Done"),
            Appearance = ControlAppearance.Primary,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 20, 0, 0),
            IsDefault = true,
            IsCancel = true,
        };
        done.Click += (_, _) =>
        {
            _settings.Hotkeys.RemoveAll(h => string.IsNullOrWhiteSpace(h.Gesture));
            Close();
        };
        root.Children.Add(done);

        Rebuild();
    }

    private void Rebuild()
    {
        _rows.Children.Clear();
        foreach (HotkeyBinding binding in _settings.Hotkeys.ToList())
        {
            _rows.Children.Add(BuildRow(binding));
        }
    }

    private UIElement BuildRow(HotkeyBinding binding)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.3, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.3, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var action = new System.Windows.Controls.ComboBox { Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        foreach (HotkeyAction a in Enum.GetValues<HotkeyAction>())
        {
            action.Items.Add(new ComboBoxItem { Content = Loc.T("HotkeyAction_" + a), Tag = a });
        }

        action.SelectedItem = action.Items.Cast<ComboBoxItem>().First(i => (HotkeyAction)i.Tag == binding.Action);
        Grid.SetColumn(action, 0);
        grid.Children.Add(action);

        var target = new System.Windows.Controls.ComboBox { Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, DisplayMemberPath = nameof(Choice.Name) };
        Grid.SetColumn(target, 1);
        grid.Children.Add(target);

        void LoadTargets()
        {
            var choices = new List<Choice>();
            if (binding.Action == HotkeyAction.SwitchToMic)
            {
                for (int i = 0; i < _settings.Mics.Count; i++)
                {
                    choices.Add(new Choice(_settings.Mics[i].Id, _settings.Mics[i].DisplayName(i)));
                }
            }
            else if (binding.Action == HotkeyAction.SwitchProfile)
            {
                choices.AddRange(_settings.Profiles.Select(p => new Choice(p.Id, p.Name)));
            }

            target.ItemsSource = choices;
            target.Visibility = choices.Count == 0 ? Visibility.Hidden : Visibility.Visible;
            string? current = binding.Action == HotkeyAction.SwitchToMic ? binding.MicId : binding.ProfileId;
            target.SelectedItem = choices.FirstOrDefault(c => c.Id == current) ?? choices.FirstOrDefault();
        }

        LoadTargets();
        action.SelectionChanged += (_, _) =>
        {
            binding.Action = (HotkeyAction)((ComboBoxItem)action.SelectedItem).Tag;
            binding.MicId = null;
            binding.ProfileId = null;
            LoadTargets();
        };
        target.SelectionChanged += (_, _) =>
        {
            string? id = (target.SelectedItem as Choice)?.Id;
            if (binding.Action == HotkeyAction.SwitchToMic)
            {
                binding.MicId = id;
            }
            else if (binding.Action == HotkeyAction.SwitchProfile)
            {
                binding.ProfileId = id;
            }
        };

        var key = new System.Windows.Controls.TextBox
        {
            IsReadOnly = true,
            Text = binding.Gesture,
            ToolTip = Loc.T("Hotkeys_Press"),
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            FlowDirection = FlowDirection.LeftToRight,
        };
        key.PreviewKeyDown += (_, e) => CaptureKey(binding, key, e);
        Grid.SetColumn(key, 2);
        grid.Children.Add(key);

        var remove = new Wpf.Ui.Controls.Button { Icon = new SymbolIcon { Symbol = SymbolRegular.Delete24 }, ToolTip = Loc.T("Hotkeys_Remove") };
        remove.Click += (_, _) =>
        {
            _settings.Hotkeys.Remove(binding);
            Rebuild();
        };
        Grid.SetColumn(remove, 3);
        grid.Children.Add(remove);
        return grid;
    }

    private void CaptureKey(HotkeyBinding binding, System.Windows.Controls.TextBox box, KeyEventArgs e)
    {
        e.Handled = true;
        Key pressed = e.Key == Key.System ? e.SystemKey : e.Key;
        if (pressed == Key.Back || pressed == Key.Delete)
        {
            binding.Gesture = string.Empty;
            box.Text = string.Empty;
            _error.Text = string.Empty;
            return;
        }

        string? name = HotkeyManager.NameFromKey(pressed);
        if (name is null)
        {
            return; // only a modifier so far
        }

        var modifiers = (HotkeyModifiers)(int)Keyboard.Modifiers;
        string gesture = HotkeyGesture.Format(modifiers, name);
        if (!HotkeyGesture.TryParse(gesture, out _, out _))
        {
            _error.Text = Loc.T("Hotkeys_NeedModifier");
            return;
        }

        HotkeyBinding? clash = _settings.Hotkeys.FirstOrDefault(h => !ReferenceEquals(h, binding) && string.Equals(h.Gesture, gesture, StringComparison.OrdinalIgnoreCase));
        if (clash is not null)
        {
            _error.Text = Loc.F("Hotkeys_InUse", gesture);
            return;
        }

        _error.Text = string.Empty;
        binding.Gesture = gesture;
        box.Text = gesture;
    }
}
