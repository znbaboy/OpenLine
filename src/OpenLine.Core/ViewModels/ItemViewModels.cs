using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenLine.Core.Audio;
using OpenLine.Core.Settings;

namespace OpenLine.Core.ViewModels;

public sealed record DeviceOption(string Id, string Name, bool IsVbCable = false)
{
    public override string ToString() => Name;
}

public interface IUiDispatcher
{
    void Post(Action action);
}

internal static class Meter
{
    /// <summary>Peak (linear) to 0..1 on a -60..0 dBFS scale, with a gentle release.</summary>
    public static double Next(double previous, float peak)
    {
        double target = peak <= 0.000001f ? 0 : Math.Clamp((20 * Math.Log10(peak) + 60) / 60, 0, 1);
        return target >= previous ? target : Math.Max(target, previous - 0.035);
    }
}

public static class Palette
{
    // Distinct hues that hold up on Mica in light and dark.
    public static readonly string[] Colors =
    [
        "#4CC2FF", "#F7A54A", "#6CCB5F", "#C58AF9", "#FF6B8B", "#3FD0C9", "#E8C547", "#8C9BFF",
    ];

    public static string At(int index) => Colors[((index % Colors.Length) + Colors.Length) % Colors.Length];
}

public sealed partial class InputViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly bool _ready;

    internal InputViewModel(MainViewModel main, InputConfig config)
    {
        _main = main;
        Config = config;
        Channel = new InputChannel(config.Id) { Gain = (float)config.Gain, Muted = config.Muted };
        Name = config.Name;
        Gain = config.Gain;
        Muted = config.Muted;
        _ready = true;
    }

    internal InputConfig Config { get; private set; }

    public InputChannel Channel { get; }

    public Guid Id => Config.Id;

    public InputKind Kind => Config.Kind;

    public string ColorHex => Palette.At(Config.ColorIndex);

    public string Glyph => Config.Kind switch
    {
        InputKind.Device => "", // Microphone
        InputKind.App => "",    // App
        _ => "",                // Speakers
    };

    [ObservableProperty]
    public partial string Name { get; set; }

    partial void OnNameChanged(string value)
    {
        Config.Name = value;
        Dirty();
    }

    public string SourceLabel => Config.Kind switch
    {
        InputKind.Device => _main.CaptureDeviceName(Config.DeviceId),
        InputKind.App => $"App: {Config.ProcessName}.exe",
        _ => "Everything you hear, except OpenLine",
    };

    /// <summary>Linear gain, 0..2.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GainPercent), nameof(GainText))]
    public partial double Gain { get; private set; }

    partial void OnGainChanged(double value)
    {
        Channel.Gain = (float)value;
        Config.Gain = value;
        Dirty();
    }

    public double GainPercent
    {
        get => Math.Round(Gain * 100);
        set => Gain = Math.Clamp(value / 100.0, 0, 2);
    }

    public string GainText => $"{Math.Round(Gain * 100)}%";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MuteGlyph))]
    public partial bool Muted { get; set; }

    partial void OnMutedChanged(bool value)
    {
        Channel.Muted = value;
        Config.Muted = value;
        Dirty();
    }

    public string MuteGlyph => Muted ? "" : "";

    [ObservableProperty]
    public partial double Level { get; private set; }

    /// <summary>Error or waiting message shown under the card. Null when healthy.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    public partial string? Status { get; internal set; }

    public bool HasStatus => !string.IsNullOrEmpty(Status);

    internal bool IsWaiting { get; set; }

    internal bool NeedsRestart { get; set; }

    [RelayCommand]
    public void RequestEdit() => _main.RaiseEditInput(this);

    [RelayCommand]
    public void RequestRename() => _main.RaiseRename(this);

    [RelayCommand]
    public void Remove() => _main.RemoveInput(this);

    internal void Tick() => Level = Meter.Next(Level, Channel.TakePeak());

    internal void ReplaceConfig(InputConfig config)
    {
        config.Id = Config.Id;
        config.ColorIndex = Config.ColorIndex;
        config.Gain = Config.Gain;
        config.Muted = Config.Muted;
        Config = config;
        Name = config.Name;
        RefreshSource();
    }

    internal void RefreshSource()
    {
        OnPropertyChanged(nameof(SourceLabel));
        OnPropertyChanged(nameof(Glyph));
        OnPropertyChanged(nameof(Kind));
    }

    private void Dirty()
    {
        if (_ready) _main.MarkDirty();
    }
}

public sealed partial class RouteViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly bool _ready;

    internal RouteViewModel(MainViewModel main, InputViewModel input, MixViewModel mix, double gain)
    {
        _main = main;
        Input = input;
        Mix = mix;
        Gain = gain;
        _ready = true;
    }

    public InputViewModel Input { get; }

    public MixViewModel Mix { get; }

    /// <summary>Linear gain of this input in this mix, 0..1.5.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GainPercent), nameof(GainText))]
    public partial double Gain { get; private set; }

    partial void OnGainChanged(double value)
    {
        if (!_ready) return; // the bus is wired up by the caller on creation
        Mix.Bus.SetRouteGain(Input.Channel, (float)value);
        _main.MarkDirty();
    }

    public double GainPercent
    {
        get => Math.Round(Gain * 100);
        set => Gain = Math.Clamp(value / 100.0, 0, 1.5);
    }

    public string GainText => $"{Math.Round(Gain * 100)}%";

    [RelayCommand]
    public void Remove() => _main.Disconnect(this);
}

public sealed partial class MixViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly bool _ready;
    private bool _isListening;
    private DeviceOption? _selectedDevice;

    internal MixViewModel(MainViewModel main, MixConfig config)
    {
        _main = main;
        Config = config;
        Bus = new MixBus(config.Id) { Volume = (float)config.Volume, Muted = config.Muted };
        Name = config.Name;
        Volume = config.Volume;
        Muted = config.Muted;
        Routes.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoRoutes));
        _ready = true;
    }

    internal MixConfig Config { get; }

    public MixBus Bus { get; }

    public Guid Id => Config.Id;

    public ObservableCollection<RouteViewModel> Routes { get; } = [];

    public ObservableCollection<DeviceOption> OutputDevices => _main.OutputDevices;

    public bool HasNoRoutes => Routes.Count == 0;

    [ObservableProperty]
    public partial string Name { get; set; }

    partial void OnNameChanged(string value)
    {
        Config.Name = value;
        Dirty();
    }

    public string Glyph => _selectedDevice?.IsVbCable == true ? "" : ""; // virtual mic / speakers

    /// <summary>
    /// Bound TwoWay to the device picker. Nulls are ignored: they only happen when the list
    /// refreshes, and the saved device id must survive an unplug.
    /// </summary>
    public DeviceOption? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (value is null || Equals(value, _selectedDevice)) return;
            _selectedDevice = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Glyph));
            OnPropertyChanged(nameof(Hint));
            if (Config.DeviceId != value.Id)
            {
                Config.DeviceId = value.Id;
                _main.MarkDirty();
                _ = _main.StartMixAsync(this);
            }
        }
    }

    public string? Hint
    {
        get
        {
            if (Config.DeviceId is null)
                return "Pick where this mix plays. Choose CABLE Input to send it to OBS, Discord, or any app as a microphone.";
            if (_selectedDevice?.IsVbCable == true)
                return "Apps see this mix as the microphone “CABLE Output (VB-Audio Virtual Cable)”.";
            return null;
        }
    }

    /// <summary>Master volume, 0..1.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VolumePercent), nameof(VolumeText))]
    public partial double Volume { get; private set; }

    partial void OnVolumeChanged(double value)
    {
        Bus.Volume = (float)value;
        Config.Volume = value;
        Dirty();
    }

    public double VolumePercent
    {
        get => Math.Round(Volume * 100);
        set => Volume = Math.Clamp(value / 100.0, 0, 1);
    }

    public string VolumeText => $"{Math.Round(Volume * 100)}%";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MuteGlyph))]
    public partial bool Muted { get; set; }

    partial void OnMutedChanged(bool value)
    {
        Bus.Muted = value;
        Config.Muted = value;
        Dirty();
    }

    public string MuteGlyph => Muted ? "" : "";

    public bool IsListening
    {
        get => _isListening;
        set
        {
            if (_isListening == value) return;
            _ = _main.SetListeningAsync(this, value);
        }
    }

    internal void SetListeningState(bool value) => SetProperty(ref _isListening, value, nameof(IsListening));

    [ObservableProperty]
    public partial double Level { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    public partial string? Status { get; internal set; }

    public bool HasStatus => !string.IsNullOrEmpty(Status);

    internal bool NeedsRestart { get; set; }

    public IEnumerable<InputViewModel> UnroutedInputs => _main.Inputs.Where(i => Routes.All(r => r.Input != i));

    public void AddRoute(InputViewModel input) => _main.Connect(input, this);

    [RelayCommand]
    public void RequestRename() => _main.RaiseRename(this);

    [RelayCommand]
    public void Remove() => _main.RemoveMix(this);

    internal void Tick() => Level = Meter.Next(Level, Bus.TakePeak());

    /// <summary>Re-select the picker entry after the device list changes.</summary>
    internal void SyncSelectedDevice()
    {
        var match = OutputDevices.FirstOrDefault(d => d.Id == Config.DeviceId);
        _selectedDevice = match;
        OnPropertyChanged(nameof(SelectedDevice));
        OnPropertyChanged(nameof(Glyph));
        OnPropertyChanged(nameof(Hint));
    }

    private void Dirty()
    {
        if (_ready) _main.MarkDirty();
    }
}
