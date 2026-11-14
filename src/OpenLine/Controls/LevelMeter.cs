using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace OpenLine.Controls;

/// <summary>Thin horizontal level bar. Level is 0..1 (already dB-scaled by the view model).</summary>
public sealed class LevelMeter : Grid
{
    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(
        nameof(Level), typeof(double), typeof(LevelMeter),
        new PropertyMetadata(0.0, (d, e) => ((LevelMeter)d)._scale.ScaleX = Math.Clamp((double)e.NewValue, 0, 1)));

    public static readonly DependencyProperty BarBrushProperty = DependencyProperty.Register(
        nameof(BarBrush), typeof(Brush), typeof(LevelMeter),
        new PropertyMetadata(null, (d, e) => ((LevelMeter)d)._bar.Fill = (Brush)e.NewValue));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(LevelMeter),
        new PropertyMetadata(null, (d, e) => ((LevelMeter)d)._track.Fill = (Brush)e.NewValue));

    private readonly Rectangle _track = new() { RadiusX = 2, RadiusY = 2 };
    private readonly Rectangle _bar = new() { RadiusX = 2, RadiusY = 2 };
    private readonly ScaleTransform _scale = new() { ScaleX = 0 };

    public LevelMeter()
    {
        Height = 4;
        IsHitTestVisible = false;
        _bar.RenderTransform = _scale;
        _bar.RenderTransformOrigin = new Point(0, 0.5);
        Children.Add(_track);
        Children.Add(_bar);
    }

    public double Level
    {
        get => (double)GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    public Brush? BarBrush
    {
        get => (Brush?)GetValue(BarBrushProperty);
        set => SetValue(BarBrushProperty, value);
    }

    public Brush? TrackBrush
    {
        get => (Brush?)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }
}
