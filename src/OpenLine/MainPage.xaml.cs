using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using OpenLine.Core.ViewModels;
using OpenLine.Dialogs;
using Windows.Foundation;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace OpenLine;

public sealed partial class MainPage : Page
{
    /// <summary>One routed connection drawn as a soft glow + a crisp cable + a wide invisible hit target.</summary>
    private sealed class Wire
    {
        public required Path Glow { get; init; }
        public required Path Line { get; init; }
        public required Path Hit { get; init; }
        public Point From { get; set; } = new(double.NaN, double.NaN);
        public Point To { get; set; } = new(double.NaN, double.NaN);
    }

    private readonly Dictionary<Guid, FrameworkElement> _inputPorts = [];
    private readonly Dictionary<Guid, FrameworkElement> _mixPorts = [];
    private readonly Dictionary<Guid, Border> _mixCards = [];
    private readonly Dictionary<RouteViewModel, Wire> _wires = [];
    private InputViewModel? _dragInput;
    private Path? _dragWire;
    private MixViewModel? _dropTarget;

    public MainPage(MainViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        ViewModel.EditInputRequested += vm => _ = EditInputAsync(vm);
        ViewModel.RenameRequested += item => _ = RenameAsync(item);
    }

    public MainViewModel ViewModel { get; }

    public UIElement TitleBarElement => AppTitleBar;

    public string AboutText => $"OpenLine {typeof(MainPage).Assembly.GetName().Version?.ToString(3)}. Free and open source under the MIT license.";

    // ---------- Frame loop (called by MainWindow ~30×/s) ----------

    public void RenderFrame()
    {
        if (!IsLoaded) return;
        SyncWires();
        foreach (var (route, wire) in _wires)
        {
            if (!_inputPorts.TryGetValue(route.Input.Id, out var ip) || !_mixPorts.TryGetValue(route.Mix.Id, out var mp) ||
                !TryCenter(ip, out var from) || !TryCenter(mp, out var to))
            {
                wire.Line.Visibility = wire.Glow.Visibility = wire.Hit.Visibility = Visibility.Collapsed;
                continue;
            }
            wire.Line.Visibility = wire.Glow.Visibility = wire.Hit.Visibility = Visibility.Visible;

            if (from != wire.From || to != wire.To)
            {
                wire.From = from;
                wire.To = to;
                wire.Line.Data = Cable(from, to);
                wire.Glow.Data = Cable(from, to);
                wire.Hit.Data = Cable(from, to);
            }

            // Signature detail: cables brighten with the signal flowing through them.
            double level = route.Input.Muted || route.Gain <= 0 ? 0 : route.Input.Level;
            wire.Line.Opacity = 0.4 + 0.6 * level;
            wire.Glow.Opacity = 0.28 * level;
        }
    }

    private void SyncWires()
    {
        var current = ViewModel.Mixes.SelectMany(m => m.Routes).ToHashSet();
        foreach (var gone in _wires.Keys.Where(r => !current.Contains(r)).ToList())
        {
            var w = _wires[gone];
            WireCanvas.Children.Remove(w.Glow);
            WireCanvas.Children.Remove(w.Line);
            WireCanvas.Children.Remove(w.Hit);
            _wires.Remove(gone);
        }
        foreach (var route in current.Where(r => !_wires.ContainsKey(r)))
        {
            var color = Ui.Brush(route.Input.ColorHex);
            var wire = new Wire
            {
                Glow = new Path { Stroke = color, StrokeThickness = 10, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, IsHitTestVisible = false },
                Line = new Path { Stroke = color, StrokeThickness = 3, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, IsHitTestVisible = false },
                Hit = new Path { Stroke = new SolidColorBrush(Microsoft.UI.Colors.Transparent), StrokeThickness = 16, Tag = route },
            };
            wire.Hit.Tapped += Wire_Tapped;
            wire.Hit.PointerEntered += (_, _) => wire.Line.StrokeThickness = 5;
            wire.Hit.PointerExited += (_, _) => wire.Line.StrokeThickness = 3;
            WireCanvas.Children.Add(wire.Glow);
            WireCanvas.Children.Add(wire.Line);
            WireCanvas.Children.Add(wire.Hit);
            _wires[route] = wire;
        }
    }

    private static PathGeometry Cable(Point from, Point to)
    {
        double dx = Math.Max(48, Math.Abs(to.X - from.X) * 0.5);
        var figure = new PathFigure { StartPoint = from, IsFilled = false };
        figure.Segments.Add(new BezierSegment
        {
            Point1 = new Point(from.X + dx, from.Y),
            Point2 = new Point(to.X - dx, to.Y),
            Point3 = to,
        });
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }

    private bool TryCenter(FrameworkElement element, out Point point)
    {
        point = default;
        if (element.XamlRoot is null || element.ActualWidth <= 0) return false;
        try
        {
            point = element.TransformToVisual(WireCanvas).TransformPoint(new Point(element.ActualWidth / 2, element.ActualHeight / 2));
            return true;
        }
        catch (Exception)
        {
            return false; // not in the tree this frame
        }
    }

    private void Body_SizeChanged(object sender, SizeChangedEventArgs e) =>
        WireCanvas.Clip = new RectangleGeometry { Rect = new Rect(0, 0, e.NewSize.Width, e.NewSize.Height) };

    // ---------- Element registry (ports and cards come and go with the templates) ----------

    private void InputPort_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: InputViewModel vm } fe) _inputPorts[vm.Id] = fe;
    }

    private void InputPort_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: InputViewModel vm } fe && _inputPorts.TryGetValue(vm.Id, out var cur) && cur == fe)
            _inputPorts.Remove(vm.Id);
    }

    private void MixPort_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: MixViewModel vm } fe) _mixPorts[vm.Id] = fe;
    }

    private void MixPort_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: MixViewModel vm } fe && _mixPorts.TryGetValue(vm.Id, out var cur) && cur == fe)
            _mixPorts.Remove(vm.Id);
    }

    private void MixCard_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is Border { DataContext: MixViewModel vm } b) _mixCards[vm.Id] = b;
    }

    private void MixCard_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is Border { DataContext: MixViewModel vm } b && _mixCards.TryGetValue(vm.Id, out var cur) && cur == b)
            _mixCards.Remove(vm.Id);
    }

    // ---------- Drag to route ----------

    private void InputPort_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: InputViewModel vm } port) return;
        var props = e.GetCurrentPoint(port).Properties;
        if (!props.IsLeftButtonPressed) return;

        _dragInput = vm;
        port.CapturePointer(e.Pointer);
        _dragWire = new Path
        {
            Stroke = Ui.Brush(vm.ColorHex),
            StrokeThickness = 3,
            StrokeDashArray = [2, 2],
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
        };
        DragCanvas.Children.Add(_dragWire);
        UpdateDrag(port, e);
        e.Handled = true;
    }

    private void InputPort_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragInput is null || sender is not FrameworkElement port) return;
        UpdateDrag(port, e);
        e.Handled = true;
    }

    private void InputPort_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_dragInput is null) return;
        if (_dropTarget is not null) ViewModel.Connect(_dragInput, _dropTarget);
        if (sender is UIElement el) el.ReleasePointerCapture(e.Pointer);
        EndDrag();
        e.Handled = true;
    }

    private void InputPort_PointerCaptureLost(object sender, PointerRoutedEventArgs e) => EndDrag();

    private void UpdateDrag(FrameworkElement port, PointerRoutedEventArgs e)
    {
        if (_dragWire is null || !TryCenter(port, out var from)) return;
        var pointer = e.GetCurrentPoint(WireCanvas).Position;

        MixViewModel? target = null;
        foreach (var mix in ViewModel.Mixes)
        {
            if (!_mixCards.TryGetValue(mix.Id, out var card)) continue;
            try
            {
                var bounds = card.TransformToVisual(WireCanvas).TransformBounds(new Rect(-24, -8, card.ActualWidth + 32, card.ActualHeight + 16));
                if (bounds.Contains(pointer)) { target = mix; break; }
            }
            catch (Exception) { }
        }
        SetDropTarget(target);

        var end = target is not null && _mixPorts.TryGetValue(target.Id, out var mp) && TryCenter(mp, out var snap) ? snap : pointer;
        _dragWire.Data = Cable(from, end);
    }

    private void SetDropTarget(MixViewModel? target)
    {
        if (target == _dropTarget) return;
        if (_dropTarget is not null && _mixCards.TryGetValue(_dropTarget.Id, out var oldCard))
        {
            oldCard.ClearValue(Border.BorderBrushProperty);
            oldCard.ClearValue(Border.BorderThicknessProperty);
        }
        _dropTarget = target;
        if (target is not null && _mixCards.TryGetValue(target.Id, out var card))
        {
            card.BorderBrush = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
            card.BorderThickness = new Thickness(2);
        }
    }

    private void EndDrag()
    {
        SetDropTarget(null);
        if (_dragWire is not null) DragCanvas.Children.Remove(_dragWire);
        _dragWire = null;
        _dragInput = null;
    }

    // ---------- Wire editing ----------

    private void Wire_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not Path { Tag: RouteViewModel route }) return;

        var flyout = new Flyout();
        var label = new TextBlock { Text = $"{route.Input.Name} in {route.Mix.Name}", Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] };
        var slider = new Slider { Minimum = 0, Maximum = 150, StepFrequency = 1, Value = route.GainPercent, Width = 240 };
        slider.ValueChanged += (_, a) => route.GainPercent = a.NewValue;
        var remove = new Button { Content = "Remove route" };
        remove.Click += (_, _) =>
        {
            flyout.Hide();
            route.Remove();
        };
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(label);
        panel.Children.Add(slider);
        panel.Children.Add(remove);
        flyout.Content = panel;
        flyout.ShowAt(WireCanvas, new FlyoutShowOptions { Position = e.GetPosition(WireCanvas) });
        e.Handled = true;
    }

    private void AddRouteMenu_Opening(object sender, object e)
    {
        if (sender is not MenuFlyout menu || menu.Target is not FrameworkElement { DataContext: MixViewModel mix }) return;
        menu.Items.Clear();
        var candidates = mix.UnroutedInputs.ToList();
        if (candidates.Count == 0)
        {
            menu.Items.Add(new MenuFlyoutItem { Text = "Every input is already in this mix", IsEnabled = false });
            return;
        }
        foreach (var input in candidates)
        {
            var item = new MenuFlyoutItem { Text = input.Name };
            item.Click += (_, _) => mix.AddRoute(input);
            menu.Items.Add(item);
        }
    }

    // ---------- Commands ----------

    private async void AddInput_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new InputEditorDialog(ViewModel, null) { XamlRoot = XamlRoot };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary && dialog.Result is { } config)
            await ViewModel.AddInputAsync(config);
    }

    private async Task EditInputAsync(InputViewModel vm)
    {
        var dialog = new InputEditorDialog(ViewModel, vm) { XamlRoot = XamlRoot };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary && dialog.Result is { } config)
            await ViewModel.ApplyInputEditAsync(vm, config);
    }

    private async Task RenameAsync(object item)
    {
        var current = item switch
        {
            InputViewModel i => i.Name,
            MixViewModel m => m.Name,
            _ => null,
        };
        if (current is null) return;
        var name = await TextPrompt.AskAsync(XamlRoot, "Rename", current, "Rename");
        if (name is null) return;
        if (item is InputViewModel input) input.Name = name;
        else if (item is MixViewModel mix) mix.Name = name;
    }

    private async void AddMix_Click(object sender, RoutedEventArgs e) => await ViewModel.AddMixAsync();

    private async void InstallVbCable_Click(object sender, RoutedEventArgs e) => await ViewModel.InstallVbCableAsync();

    private async void VbBannerAction_Click(object sender, RoutedEventArgs e) => await ViewModel.VbBannerActionAsync();

    private async void RestartWindows_Click(object sender, RoutedEventArgs e)
    {
        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"],
            Title = "Restart Windows now?",
            Content = "Save your work first. Windows restarts in 5 seconds.",
            PrimaryButtonText = "Restart",
            CloseButtonText = "Not now",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await confirm.ShowAsync() == ContentDialogResult.Primary) ViewModel.RestartWindows();
    }
}
