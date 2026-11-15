using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenLine.Core.Audio;
using OpenLine.Core.Settings;
using OpenLine.Core.ViewModels;

namespace OpenLine.Dialogs;

/// <summary>Add an input, or change an existing input's source.</summary>
public sealed class InputEditorDialog : ContentDialog
{
    private readonly MainViewModel _vm;
    private readonly Segmented _kind;
    private readonly ComboBox _devices;
    private readonly ComboBox _apps;
    private readonly Grid _appRow;
    private readonly TextBlock _systemHint;
    private readonly TextBox _name;
    private readonly TextBlock _error;
    private readonly string? _initialProcess;
    private bool _nameTouched;
    private bool _settingName;

    public InputEditorDialog(MainViewModel vm, InputViewModel? existing)
    {
        _vm = vm;
        Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"];
        Title = existing is null ? "Add input" : "Change source";
        PrimaryButtonText = existing is null ? "Add input" : "Save";
        CloseButtonText = "Cancel";
        DefaultButton = ContentDialogButton.Primary;

        // Order matches InputKind: Device, App, System.
        _kind = new Segmented { HorizontalAlignment = HorizontalAlignment.Stretch };
        _kind.Items.Add(new SegmentedItem { Content = "Microphone", Icon = new FontIcon { Glyph = "\uE720" } });
        _kind.Items.Add(new SegmentedItem { Content = "App", Icon = new FontIcon { Glyph = "\uECAA" } });
        _kind.Items.Add(new SegmentedItem { Content = "Desktop audio", Icon = new FontIcon { Glyph = "\uE7F5" } });
        var kindPanel = new StackPanel { Spacing = 8 };
        kindPanel.Children.Add(new TextBlock { Text = "Source" });
        kindPanel.Children.Add(_kind);

        _devices = new ComboBox { Header = "Device", HorizontalAlignment = HorizontalAlignment.Stretch, ItemsSource = vm.CaptureDevices };

        _apps = new ComboBox
        {
            Header = "App",
            IsEditable = true,
            PlaceholderText = "Pick a running app or type its name",
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var refresh = new Button
        {
            Content = new FontIcon { Glyph = "\uE72C", FontSize = 14 },
            VerticalAlignment = VerticalAlignment.Bottom,
            Width = 36,
            Height = 32,
            Padding = new Thickness(0),
        };
        ToolTipService.SetToolTip(refresh, "Refresh the app list");
        refresh.Click += async (_, _) => await LoadAppsAsync();
        _appRow = new Grid { ColumnSpacing = 8 };
        _appRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _appRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _appRow.Children.Add(_apps);
        Grid.SetColumn(refresh, 1);
        _appRow.Children.Add(refresh);

        _systemHint = new TextBlock
        {
            Text = "Captures everything playing on this PC except OpenLine, so a mix can never echo back into itself. Apps only show up here once they've played sound.",
            TextWrapping = TextWrapping.Wrap,
            Style = (Style)Application.Current.Resources["SecondaryCaptionStyle"],
        };

        _name = new TextBox { Header = "Name", MaxLength = 40 };
        _name.TextChanged += (_, _) => { if (!_settingName) _nameTouched = true; };

        _error = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
            Visibility = Visibility.Collapsed,
        };

        var panel = new StackPanel { Spacing = 16, Width = 380 };
        panel.Children.Add(kindPanel);
        panel.Children.Add(_devices);
        panel.Children.Add(_appRow);
        panel.Children.Add(_systemHint);
        panel.Children.Add(_name);
        panel.Children.Add(_error);
        Content = panel;

        // Initial state
        var kind = existing?.Kind ?? InputKind.Device;
        _kind.SelectedIndex = (int)kind;
        if (existing is not null)
        {
            _nameTouched = true;
            SetName(existing.Name);
            var config = vm.GetInputConfig(existing);
            _devices.SelectedItem = vm.CaptureDevices.FirstOrDefault(d => d.Id == (config.DeviceId ?? DeviceIds.Default));
            _initialProcess = config.ProcessName;
        }
        else
        {
            _devices.SelectedIndex = vm.CaptureDevices.Count > 0 ? 0 : -1;
            SetName("Microphone");
        }

        _kind.SelectionChanged += (_, _) => { UpdateVisibility(); SuggestName(); };
        _devices.SelectionChanged += (_, _) => SuggestName();
        _apps.SelectionChanged += (_, _) => SuggestName();
        Opened += async (_, _) => await LoadAppsAsync();
        PrimaryButtonClick += OnPrimary;
        UpdateVisibility();
    }

    /// <summary>Set when the user confirms.</summary>
    public InputConfig? Result { get; private set; }

    private InputKind SelectedKind => (InputKind)Math.Max(0, _kind.SelectedIndex);

    private async Task LoadAppsAsync()
    {
        var keepText = _apps.SelectedItem is AudioAppInfo sel ? sel.ProcessName : (_apps.Text ?? _initialProcess);
        var apps = await _vm.GetAudioAppsAsync();
        _apps.ItemsSource = apps;
        var match = apps.FirstOrDefault(a => string.Equals(a.ProcessName, keepText, StringComparison.OrdinalIgnoreCase));
        if (match is not null) _apps.SelectedItem = match;
        else if (!string.IsNullOrEmpty(keepText)) _apps.Text = keepText;
    }

    private void UpdateVisibility()
    {
        var k = SelectedKind;
        _devices.Visibility = k == InputKind.Device ? Visibility.Visible : Visibility.Collapsed;
        _appRow.Visibility = k == InputKind.App ? Visibility.Visible : Visibility.Collapsed;
        _systemHint.Visibility = k == InputKind.System ? Visibility.Visible : Visibility.Collapsed;
        _error.Visibility = Visibility.Collapsed;
    }

    private void SuggestName()
    {
        if (_nameTouched) return;
        SetName(SelectedKind switch
        {
            InputKind.App when _apps.SelectedItem is AudioAppInfo a => a.DisplayName,
            InputKind.App => "App",
            InputKind.System => "Desktop audio",
            _ => "Microphone",
        });
    }

    private void SetName(string name)
    {
        _settingName = true;
        _name.Text = name;
        _settingName = false;
    }

    private void OnPrimary(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var kind = SelectedKind;
        string? process = null;
        if (kind == InputKind.App)
        {
            process = _apps.SelectedItem is AudioAppInfo a ? a.ProcessName : _apps.Text?.Trim();
            if (process is not null && process.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) process = process[..^4];
            if (string.IsNullOrWhiteSpace(process))
            {
                _error.Text = "Pick an app, or type its program name (for example: spotify).";
                _error.Visibility = Visibility.Visible;
                args.Cancel = true;
                return;
            }
        }

        var name = _name.Text.Trim();
        Result = new InputConfig
        {
            Kind = kind,
            Name = name.Length > 0 ? name : "Input",
            DeviceId = kind == InputKind.Device ? (_devices.SelectedItem as DeviceOption)?.Id ?? DeviceIds.Default : null,
            ProcessName = process,
        };
    }
}
