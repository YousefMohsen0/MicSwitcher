using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Microsoft.Win32;
using MicSwitcher.Core;
using Wpf.Ui.Controls;

namespace MicSwitcher.App;

/// <summary>Small dialog to rename a mic and choose its icon (built-in or a picture from disk).</summary>
public sealed class MicCustomizeDialog : FluentWindow
{
    private readonly MicRole _role;
    private readonly System.Windows.Controls.TextBox _name = new();
    private readonly TextBlock _fileText = new();
    private readonly TextBlock _error = new();
    private readonly WrapPanel _presets = new();

    private string? _iconKey;
    private string? _iconPath;

    public MicCustomizeDialog(MicRole role, MicSettings current)
    {
        _role = role;
        _iconKey = current.IconKey;
        _iconPath = current.IconPath;

        Title = "Customize " + MicAppearance.DefaultName(role).ToLowerInvariant();
        Width = 480;
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

        root.Children.Add(Label("Name"));
        _name.Text = current.Label ?? string.Empty;
        _name.MaxLength = 40;
        _name.Tag = MicAppearance.DefaultName(role);
        root.Children.Add(_name);
        root.Children.Add(Hint($"Leave empty to use \"{MicAppearance.DefaultName(role)}\"."));

        root.Children.Add(Label("Icon"));
        foreach (SymbolRegular symbol in MicAppearance.Presets)
        {
            var toggle = new ToggleButton
            {
                Width = 44,
                Height = 44,
                Margin = new Thickness(0, 0, 8, 8),
                Content = new SymbolIcon { Symbol = symbol, FontSize = 22 },
                Tag = symbol.ToString(),
                ToolTip = symbol.ToString().Replace("24", string.Empty),
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

        var pick = new Wpf.Ui.Controls.Button { Content = "Choose a picture…", Margin = new Thickness(0, 4, 0, 0) };
        pick.Click += (_, _) => PickPicture();
        root.Children.Add(pick);
        _fileText.Margin = new Thickness(0, 8, 0, 0);
        _fileText.FontSize = 12;
        _fileText.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        root.Children.Add(_fileText);
        _error.Foreground = System.Windows.Media.Brushes.IndianRed;
        _error.TextWrapping = TextWrapping.Wrap;
        root.Children.Add(_error);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        var reset = new Wpf.Ui.Controls.Button { Content = "Reset to default", Margin = new Thickness(0, 0, 8, 0) };
        reset.Click += (_, _) =>
        {
            _name.Text = string.Empty;
            _iconKey = null;
            _iconPath = null;
            SyncSelection();
        };
        var cancel = new Wpf.Ui.Controls.Button { Content = "Cancel", Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
        var save = new Wpf.Ui.Controls.Button { Content = "Save", Appearance = ControlAppearance.Primary, IsDefault = true };
        save.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(reset);
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        root.Children.Add(buttons);

        SyncSelection();
    }

    public string? ResultLabel => string.IsNullOrWhiteSpace(_name.Text) ? null : _name.Text.Trim();
    public string? ResultIconKey => _iconKey;
    public string? ResultIconPath => _iconPath;

    private static TextBlock Label(string text) =>
        new() { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 6) };

    private TextBlock Hint(string text)
    {
        var block = new TextBlock { Text = text, FontSize = 12, Margin = new Thickness(0, 6, 0, 0) };
        block.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        return block;
    }

    private void SyncSelection()
    {
        foreach (ToggleButton toggle in _presets.Children)
        {
            toggle.IsChecked = _iconPath is null && (string?)toggle.Tag == (_iconKey ?? MicAppearance.DefaultIcon(_role).ToString());
        }

        _fileText.Text = _iconPath is null ? string.Empty : "Using your picture: " + System.IO.Path.GetFileName(_iconPath);
        _error.Text = string.Empty;
    }

    private void PickPicture()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose a picture",
            Filter = "Pictures (*.png;*.jpg;*.jpeg;*.bmp;*.ico;*.gif)|*.png;*.jpg;*.jpeg;*.bmp;*.ico;*.gif|All files (*.*)|*.*",
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        string? imported = MicAppearance.Import(_role, dialog.FileName, out string? error);
        if (imported is null)
        {
            _error.Text = error ?? string.Empty;
            return;
        }

        _iconPath = imported;
        SyncSelection();
    }
}
