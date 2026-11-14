using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
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

public sealed class InputViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private string _name;
    private double _gain;
    private bool _muted;
    private double _level;
    private string? _status;
    private bool _isWaiting;

    internal InputViewModel(MainViewModel main, InputConfig config)
    {
        _main = main;
        Config = config;
        _name = config.Name;
        _gain = config.Gain;
        _muted = config.Muted;
        Channel = new InputChannel(config.Id) { Gain = (float)config.Gain, Muted = config.Muted };
    }

    internal InputConfig Config { get; private set; }

    public InputChannel Channel { get; }

    public Guid Id => Config.Id;

    public InputKind Kind => Config.Kind;

    public string ColorHex => Palette.At(Config.ColorIndex);

    public string Glyph => Config.Kind switch
    {
        InputKind.Device => "\uE720", // Microphone
        InputKind.App => "\uECAA",    // App
        _ => "\uE7F5",                // Speakers
    };

    public string Name
    {
        get => _name;
        set
        {
            if (SetProperty(ref _name, value))
            {
                Config.Name = value;
                _main.MarkDirty();
            }
        }
    }

    public string SourceLabel => Config.Kind switch
    {
        InputKind.Device => _main.CaptureDeviceName(Config.DeviceId),
        InputKind.App => $"App: {Config.ProcessName}.exe",
        _ => "Everything you hear, except OpenLine",
    };

    public double GainPercent
    {
        get => Math.Round(_gain * 100);
        set
        {
            var g = Math.Clamp(value / 100.0, 0, 2);
            if (SetProperty(ref _gain, g))
            {
                Channel.Gain = (float)g;
                Config.Gain = g;
                OnPropertyChanged(nameof(GainText));
                _main.MarkDirty();
            }
        }
    }

    public string GainText => $"{Math.Round(_gain * 100)}%";

    public bool Muted
    {
        get => _muted;
        set
        {
            if (SetProperty(ref _muted, value))
            {
                Channel.Muted = value;
                Config.Muted = value;
                OnPropertyChanged(nameof(MuteGlyph));
                _main.MarkDirty();
            }
        }
    }

    public string MuteGlyph => _muted ? "\uE74F" : "\uE767";

    public double Level
    {
        get => _level;
        private set => SetProperty(ref _level, value);
    }

    /// <summary>Error or waiting message shown under the card. Null when healthy.</summary>
    public string? Status
    {
        get => _status;
        internal set
        {
            if (SetProperty(ref _status, value)) OnPropertyChanged(nameof(HasStatus));
        }
    }

    public bool HasStatus => !string.IsNullOrEmpty(_status);

    internal bool IsWaiting
    {
        get => _isWaiting;
        set => _isWaiting = value;
    }

    internal bool NeedsRestart { get; set; }

    public void RequestEdit() => _main.RaiseEditInput(this);

    public void RequestRename() => _main.RaiseRename(this);

    public void Remove() => _main.RemoveInput(this);

    internal void Tick() => Level = Meter.Next(_level, Channel.TakePeak());

    internal void ReplaceConfig(InputConfig config)
    {
        config.Id = Config.Id;
        config.ColorIndex = Config.ColorIndex;
        config.Gain = Config.Gain;
        config.Muted = Config.Muted;
        Config = config;
        _name = config.Name;
        OnPropertyChanged(nameof(Name));
        RefreshSource();
    }

    internal void RefreshSource()
    {
        OnPropertyChanged(nameof(SourceLabel));
        OnPropertyChanged(nameof(Glyph));
        OnPropertyChanged(nameof(Kind));
    }
}

public sealed class RouteViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private double _gain;

    internal RouteViewModel(MainViewModel main, InputViewModel input, MixViewModel mix, double gain)
    {
        _main = main;
        Input = input;
        Mix = mix;
        _gain = gain;
    }

    public InputViewModel Input { get; }

    public MixViewModel Mix { get; }

    public double GainPercent
    {
        get => Math.Round(_gain * 100);
        set
        {
            var g = Math.Clamp(value / 100.0, 0, 1.5);
            if (SetProperty(ref _gain, g))
            {
                Mix.Bus.SetRouteGain(Input.Channel, (float)g);
                OnPropertyChanged(nameof(GainText));
                _main.MarkDirty();
            }
        }
    }

    public double Gain => _gain;

    public string GainText => $"{Math.Round(_gain * 100)}%";

    public void Remove() => _main.Disconnect(this);
}

public sealed class MixViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private string _name;
    private double _volume;
    private bool _muted;
    private bool _isListening;
    private double _level;
    private string? _status;
    private DeviceOption? _selectedDevice;
    private bool _monoSafe;

    internal MixViewModel(MainViewModel main, MixConfig config)
    {
        _main = main;
        Config = config;
        _name = config.Name;
        _volume = config.Volume;
        _muted = config.Muted;
        _monoSafe = config.MonoSafe;
        Bus = new MixBus(config.Id) { Volume = (float)config.Volume, Muted = config.Muted, MonoSafe = config.MonoSafe };
        Routes.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoRoutes));
    }

    internal MixConfig Config { get; }

    public MixBus Bus { get; }

    public Guid Id => Config.Id;

    public ObservableCollection<RouteViewModel> Routes { get; } = [];

    public ObservableCollection<DeviceOption> OutputDevices => _main.OutputDevices;

    public bool HasNoRoutes => Routes.Count == 0;

    public string Name
    {
        get => _name;
        set
        {
            if (SetProperty(ref _name, value))
            {
                Config.Name = value;
                _main.MarkDirty();
            }
        }
    }

    public string Glyph => _selectedDevice?.IsVbCable == true ? "\uE720" : "\uE7F5"; // virtual mic / speakers

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

    public double VolumePercent
    {
        get => Math.Round(_volume * 100);
        set
        {
            var v = Math.Clamp(value / 100.0, 0, 1);
            if (SetProperty(ref _volume, v))
            {
                Bus.Volume = (float)v;
                Config.Volume = v;
                OnPropertyChanged(nameof(VolumeText));
                _main.MarkDirty();
            }
        }
    }

    public string VolumeText => $"{Math.Round(_volume * 100)}%";

    public bool Muted
    {
        get => _muted;
        set
        {
            if (SetProperty(ref _muted, value))
            {
                Bus.Muted = value;
                Config.Muted = value;
                OnPropertyChanged(nameof(MuteGlyph));
                _main.MarkDirty();
            }
        }
    }

    public string MuteGlyph => _muted ? "\uE74F" : "\uE767";

    public bool MonoSafe
    {
        get => _monoSafe;
        set
        {
            if (SetProperty(ref _monoSafe, value))
            {
                Bus.MonoSafe = value;
                Config.MonoSafe = value;
                _main.MarkDirty();
            }
        }
    }

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

    public double Level
    {
        get => _level;
        private set => SetProperty(ref _level, value);
    }

    public string? Status
    {
        get => _status;
        internal set
        {
            if (SetProperty(ref _status, value)) OnPropertyChanged(nameof(HasStatus));
        }
    }

    public bool HasStatus => !string.IsNullOrEmpty(_status);

    internal bool NeedsRestart { get; set; }

    public IEnumerable<InputViewModel> UnroutedInputs => _main.Inputs.Where(i => Routes.All(r => r.Input != i));

    public void AddRoute(InputViewModel input) => _main.Connect(input, this);

    public void RequestRename() => _main.RaiseRename(this);

    public void Remove() => _main.RemoveMix(this);

    internal void Tick() => Level = Meter.Next(_level, Bus.TakePeak());

    /// <summary>Re-select the picker entry after the device list changes.</summary>
    internal void SyncSelectedDevice()
    {
        var match = OutputDevices.FirstOrDefault(d => d.Id == Config.DeviceId);
        _selectedDevice = match;
        OnPropertyChanged(nameof(SelectedDevice));
        OnPropertyChanged(nameof(Glyph));
        OnPropertyChanged(nameof(Hint));
    }
}
