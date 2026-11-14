using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Microsoft.Win32;
using MicSwitcher.App.Audio;
using MicSwitcher.Core;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;

namespace MicSwitcher.App;

/// <summary>Dialog to name a mic, choose its icon, mark it as a fallback and pick the output (with volume) to use with it.</summary>
public sealed class MicCustomizeDialog : FluentWindow
{
    private sealed record OutputItem(string? Id, string Name);

    private readonly MicSlot _slot;
    private readonly System.Windows.Controls.TextBox _name = new();
    private readonly TextBlock _fileText = new();
    private readonly TextBlock _error = new();
    private readonly WrapPanel _presets = new();
    private readonly System.Windows.Controls.CheckBox _fallback = new();
    private readonly System.Windows.Controls.ComboBox _output = new();
    private readonly Slider _volume = new() { Minimum = 0, Maximum = 100, TickFrequency = 5, IsSnapToTickEnabled = true };
    private readonly System.Windows.Controls.CheckBox _setVolume = new();
    private readonly TextBlock _volumeText = new() { Width = 48, VerticalAlignment = VerticalAlignment.Center };

    private string? _iconKey;
    private string? _iconPath;

    public MicCustomizeDialog(MicSlot slot, IReadOnlyList<CaptureDevice> outputs)
    {
        _slot = slot;
        _iconKey = slot.IconKey;
        _iconPath = slot.IconPath;

        FlowDirection = Loc.Direction;
        Title = Loc.T("Customize_Title");
        Width = 500;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ExtendsContentIntoTitleBar = true;
        WindowBackdropType = AppTheme.IsBlack ? WindowBackdropType.None : WindowBackdropType.Mica;
        if (AppTheme.IsBlack)
        {
            Background = System.Windows.Media.Brushes.Black;
        }

        var root = new StackPanel { Margin = new Thickness(24, 0, 24, 22) };
        var panel = new Grid();
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition());
        panel.Children.Add(new TitleBar { Title = Title });
        Grid.SetRow(root, 1);
        panel.Children.Add(root);
        Content = panel;

        root.Children.Add(Label(Loc.T("Customize_Name")));
        _name.Text = slot.Label ?? string.Empty;
        _name.MaxLength = 40;
        root.Children.Add(_name);
        root.Children.Add(Hint(Loc.T("Customize_NameHint")));

        root.Children.Add(Label(Loc.T("Customize_Icon")));
        foreach (SymbolRegular symbol in MicAppearance.Presets)
        {
            var toggle = new ToggleButton
            {
                Width = 44,
                Height = 44,
                Margin = new Thickness(0, 0, 8, 8),
                Content = new SymbolIcon { Symbol = symbol, FontSize = 22 },
                Tag = symbol.ToString(),
            };
            toggle.Click += (_, _) =>
            {
                _iconKey = (string)toggle.Tag;
                _iconPath = null;
                SyncSelection();
            };
            _presets.Children.Add(toggle);
        }

        root.Children.Add(_presets);
        var pick = new Wpf.Ui.Controls.Button { Content = Loc.T("Customize_Picture"), Margin = new Thickness(0, 4, 0, 0) };
        pick.Click += (_, _) => PickPicture();
        root.Children.Add(pick);
        _fileText.Margin = new Thickness(0, 8, 0, 0);
        _fileText.FontSize = 12;
        _fileText.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        root.Children.Add(_fileText);
        _error.Foreground = System.Windows.Media.Brushes.IndianRed;
        _error.TextWrapping = TextWrapping.Wrap;
        root.Children.Add(_error);

        _fallback.Content = Loc.T("Customize_Fallback");
        _fallback.IsChecked = slot.Fallback;
        _fallback.Margin = new Thickness(0, 16, 0, 0);
        root.Children.Add(_fallback);
        root.Children.Add(Hint(Loc.T("Customize_FallbackHint")));

        root.Children.Add(Label(Loc.T("Customize_Output")));
        var items = new List<OutputItem> { new(null, Loc.T("Customize_OutputNone")) };
        items.AddRange(outputs.Select(o => new OutputItem(o.Id, o.Name)));
        if (slot.OutputDeviceId is not null && items.All(i => i.Id != slot.OutputDeviceId))
        {
            items.Add(new OutputItem(slot.OutputDeviceId, (slot.OutputDeviceName ?? "?") + Loc.T("Row_MissingSuffix")));
        }

        _output.ItemsSource = items;
        _output.DisplayMemberPath = nameof(OutputItem.Name);
        _output.SelectedItem = items.FirstOrDefault(i => i.Id == slot.OutputDeviceId) ?? items[0];
        root.Children.Add(_output);
        root.Children.Add(Hint(Loc.T("Customize_OutputHint")));

        _setVolume.Content = Loc.T("Customize_SetVolume");
        _setVolume.IsChecked = slot.OutputVolumePercent is not null;
        _setVolume.Margin = new Thickness(0, 10, 0, 0);
        root.Children.Add(_setVolume);

        _volume.Value = slot.OutputVolumePercent ?? 50;
        _volume.Margin = new Thickness(0, 4, 8, 0);
        var volumeRow = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(_volumeText, Dock.Right);
        volumeRow.Children.Add(_volumeText);
        volumeRow.Children.Add(_volume);
        root.Children.Add(volumeRow);
        _volume.ValueChanged += (_, _) => SyncVolume();
        _setVolume.Click += (_, _) => SyncVolume();
        _output.SelectionChanged += (_, _) => SyncVolume();

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        var reset = new Wpf.Ui.Controls.Button { Content = Loc.T("Customize_Reset"), Margin = new Thickness(0, 0, 8, 0) };
        reset.Click += (_, _) =>
        {
            _name.Text = string.Empty;
            _iconKey = null;
            _iconPath = null;
            SyncSelection();
        };
        var cancel = new Wpf.Ui.Controls.Button { Content = Loc.T("Common_Cancel"), Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
        var save = new Wpf.Ui.Controls.Button { Content = Loc.T("Common_Save"), Appearance = ControlAppearance.Primary, IsDefault = true };
        save.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(reset);
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        root.Children.Add(buttons);

        SyncSelection();
        SyncVolume();
    }

    public string? ResultLabel => string.IsNullOrWhiteSpace(_name.Text) ? null : _name.Text.Trim();
    public string? ResultIconKey => _iconKey;
    public string? ResultIconPath => _iconPath;
    public bool ResultFallback => _fallback.IsChecked == true;
    public string? ResultOutputId => (_output.SelectedItem as OutputItem)?.Id;
    public string? ResultOutputName => ResultOutputId is null ? null : (_output.SelectedItem as OutputItem)?.Name;
    public int? ResultVolume => ResultOutputId is not null && _setVolume.IsChecked == true ? (int)Math.Round(_volume.Value) : null;

    private static TextBlock Label(string text) =>
        new() { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 6) };

    private static TextBlock Hint(string text)
    {
        var block = new TextBlock { Text = text, FontSize = 12, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap };
        block.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        return block;
    }

    private void SyncVolume()
    {
        bool hasOutput = ResultOutputId is not null;
        _setVolume.IsEnabled = hasOutput;
        _volume.IsEnabled = hasOutput && _setVolume.IsChecked == true;
        _volumeText.Text = $"{_volume.Value:0}%";
    }

    private void SyncSelection()
    {
        string current = _iconKey ?? (_slot.Fallback ? Wpf.Ui.Controls.SymbolRegular.Laptop24 : Wpf.Ui.Controls.SymbolRegular.Mic24).ToString();
        foreach (ToggleButton toggle in _presets.Children)
        {
            toggle.IsChecked = _iconPath is null && (string?)toggle.Tag == current;
        }

        _fileText.Text = _iconPath is null ? string.Empty : Loc.F("Customize_UsingPicture", System.IO.Path.GetFileName(_iconPath));
        _error.Text = string.Empty;
    }

    private void PickPicture()
    {
        var dialog = new OpenFileDialog
        {
            Title = Loc.T("Customize_Picture"),
            Filter = "Pictures (*.png;*.jpg;*.jpeg;*.bmp;*.ico;*.gif)|*.png;*.jpg;*.jpeg;*.bmp;*.ico;*.gif|All files (*.*)|*.*",
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        string? imported = MicAppearance.Import(_slot.Id, dialog.FileName, out string? error);
        if (imported is null)
        {
            _error.Text = error ?? string.Empty;
            return;
        }

        _iconPath = imported;
        SyncSelection();
    }
}
