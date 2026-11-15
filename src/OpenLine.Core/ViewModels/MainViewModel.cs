using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NAudio.CoreAudioApi;
using OpenLine.Core.Audio;
using OpenLine.Core.Settings;
using OpenLine.Core.Setup;
using OpenLine.Core.Shell;

namespace OpenLine.Core.ViewModels;

public enum VbCableState { Unknown, Missing, Disabled, Downloading, WaitingForSetup, RestartRequired, Ready }

public sealed partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(600);
    private static readonly TimeSpan AppPollInterval = TimeSpan.FromSeconds(3);

    private readonly AudioEngine _engine;
    private readonly SettingsStore _store;
    private readonly IUiDispatcher _ui;
    private AppSettings _settings = new();
    private readonly List<AudioDeviceInfo> _captureInfo = [];
    private DateTime? _saveDue;
    private DateTime? _deviceRefreshDue;
    private DateTime _nextAppPoll = DateTime.MinValue;
    private bool _defaultCaptureChanged, _defaultRenderChanged;
    private bool _initialized;
    private bool _showVbBanner = true;
    private DeviceOption? _monitorDevice;
    private bool _loadingSettings;

    public MainViewModel(AudioEngine engine, SettingsStore store, IUiDispatcher ui)
    {
        _engine = engine;
        _store = store;
        _ui = ui;
        _engine.DeviceChanged += change => _ui.Post(() => OnDeviceChanged(change));
        Inputs.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoInputs));
        Mixes.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoMixes));
        _loadingSettings = true;
        CloseToTray = true;
        _loadingSettings = false;
    }

    public ObservableCollection<InputViewModel> Inputs { get; } = [];

    public ObservableCollection<MixViewModel> Mixes { get; } = [];

    public ObservableCollection<DeviceOption> OutputDevices { get; } = [];

    public ObservableCollection<DeviceOption> CaptureDevices { get; } = [];

    public bool HasNoInputs => Inputs.Count == 0;

    public bool HasNoMixes => Mixes.Count == 0;

    /// <summary>Raised when a card asks for the source editor.</summary>
    public event Action<InputViewModel>? EditInputRequested;

    /// <summary>Raised when a card asks to rename (InputViewModel or MixViewModel).</summary>
    public event Action<object>? RenameRequested;

    /// <summary>Raised when routes are added/removed so the view can redraw wires.</summary>
    public event Action? RoutesChanged;

    // ---------- Settings surface ----------

    public DeviceOption? MonitorDevice
    {
        get => _monitorDevice;
        set
        {
            if (value is null || Equals(value, _monitorDevice)) return;
            _monitorDevice = value;
            OnPropertyChanged();
            _settings.MonitorDeviceId = value.Id;
            MarkDirty();
            var listening = Mixes.FirstOrDefault(m => m.IsListening);
            if (listening is not null) _ = SetListeningAsync(listening, true);
        }
    }

    [ObservableProperty]
    public partial bool CloseToTray { get; set; }

    partial void OnCloseToTrayChanged(bool value)
    {
        _settings.CloseToTray = value;
        if (!_loadingSettings) MarkDirty();
    }

    public bool StartWithWindows
    {
        get => Autostart.IsEnabled;
        set
        {
            try { Autostart.Set(value); }
            catch (Exception ex) { ErrorMessage = $"Couldn't change startup setting: {ex.Message}"; }
            OnPropertyChanged();
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; set; }

    public bool HasError
    {
        get => !string.IsNullOrEmpty(ErrorMessage);
        set { if (!value) ErrorMessage = null; }
    }

    // ---------- VB-Cable ----------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowVbMissing), nameof(ShowVbBusy), nameof(ShowVbRestart), nameof(VbStatusText),
        nameof(VbButtonText), nameof(CanInstallVb), nameof(VbIndeterminate), nameof(VbBusyText),
        nameof(VbMissingTitle), nameof(VbMissingMessage), nameof(VbMissingAction))]
    [NotifyCanExecuteChangedFor(nameof(InstallVbCableCommand), nameof(VbBannerActionCommand))]
    public partial VbCableState VbCable { get; private set; }

    public bool VbIndeterminate => VbCable == VbCableState.WaitingForSetup || VbProgress <= 0;

    public string VbMissingAction => VbCable == VbCableState.Disabled ? "Open Sound settings" : "Install VB-Cable";

    /// <summary>Banner action: install, or open Sound settings when the cable is just turned off.</summary>
    [RelayCommand(CanExecute = nameof(CanInstallVb))]
    public async Task VbBannerActionAsync()
    {
        if (VbCable == VbCableState.Disabled)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:sound") { UseShellExecute = true }); }
            catch (Exception ex) { ErrorMessage = $"Couldn't open Sound settings: {ex.Message}"; }
            return;
        }
        await InstallVbCableAsync();
    }

    public bool ShowVbMissing
    {
        get => _showVbBanner && VbCable is VbCableState.Missing or VbCableState.Disabled;
        set
        {
            if (!value) _showVbBanner = false;
            OnPropertyChanged();
        }
    }

    public bool ShowVbBusy => VbCable is VbCableState.Downloading or VbCableState.WaitingForSetup;

    public bool ShowVbRestart
    {
        get => VbCable == VbCableState.RestartRequired;
        set { /* closable InfoBar: state stays until devices change */ }
    }

    public bool CanInstallVb => !ShowVbBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VbIndeterminate))]
    public partial double VbProgress { get; private set; }

    public string VbBusyText => VbCable == VbCableState.Downloading
        ? "Downloading VB-Cable from vb-audio.com…"
        : "Finish in the VB-Cable window: click Install Driver, then close it.";

    public string VbMissingTitle => VbCable == VbCableState.Disabled ? "VB-Cable is turned off" : "VB-Cable isn't installed";

    public string VbMissingMessage => VbCable == VbCableState.Disabled
        ? "Turn on “CABLE Input” in Windows Sound settings (Sound > All sound devices) so the Stream mix can reach other apps."
        : "Install it so a mix can show up as a microphone (CABLE Output) in OBS, Discord, and other apps.";

    public string VbStatusText => VbCable switch
    {
        VbCableState.Ready => "Installed and ready. Apps can use “CABLE Output” as a microphone.",
        VbCableState.Disabled => "Installed, but turned off in Windows Sound settings.",
        VbCableState.RestartRequired => "Installed. Restart Windows to finish.",
        VbCableState.Downloading or VbCableState.WaitingForSetup => "Installing…",
        _ => "Not installed. It's needed to send a mix to other apps as a microphone.",
    };

    public string VbButtonText => VbCable is VbCableState.Ready or VbCableState.Disabled or VbCableState.RestartRequired
        ? "Repair or remove VB-Cable"
        : "Install VB-Cable";

    [RelayCommand(CanExecute = nameof(CanInstallVb), AllowConcurrentExecutions = true)]
    public async Task InstallVbCableAsync()
    {
        if (ShowVbBusy) return;
        var before = VbCable;
        try
        {
            ErrorMessage = null;
            VbProgress = 0;
            VbCable = VbCableState.Downloading;
            var progress = new Progress<double>(p => VbProgress = p * 100);
            var exe = await VbCableInstaller.DownloadAsync(progress, CancellationToken.None);

            VbCable = VbCableState.WaitingForSetup;
            if (!await VbCableInstaller.RunSetupAsync(exe, CancellationToken.None))
            {
                VbCable = before;
                return;
            }

            await Task.Delay(1500); // let the endpoint register
            var presence = await _engine.GetVbCablePresenceAsync();
            if (presence == VbCablePresence.Ready)
            {
                VbCable = VbCableState.Ready;
                await RefreshDevicesAsync();
                AutoAssignCable();
            }
            else
            {
                VbCable = before == VbCableState.Ready ? VbCableState.Ready : VbCableState.RestartRequired;
            }
        }
        catch (Exception ex)
        {
            VbCable = before;
            ErrorMessage = $"VB-Cable install didn't finish: {ex.Message}";
        }
    }

    public void RestartWindows()
    {
        try { VbCableInstaller.RestartWindows(); }
        catch (Exception ex) { ErrorMessage = $"Couldn't restart: {ex.Message}"; }
    }

    // ---------- Lifecycle ----------

    public async Task InitializeAsync()
    {
        if (_initialized) return;
        _initialized = true;

        _settings = _store.Load();
        _loadingSettings = true;
        CloseToTray = _settings.CloseToTray;
        _loadingSettings = false;
        try { Autostart.Repair(); } catch { }

        await RefreshDevicesAsync();
        await RefreshVbCableAsync();

        if (!_settings.FirstRunDone) CreateDefaults();

        foreach (var cfg in _settings.Inputs) Inputs.Add(new InputViewModel(this, cfg));
        foreach (var cfg in _settings.Mixes)
        {
            var mix = new MixViewModel(this, cfg);
            Mixes.Add(mix);
            foreach (var r in cfg.Routes)
            {
                var input = Inputs.FirstOrDefault(i => i.Id == r.InputId);
                if (input is null) continue;
                mix.Routes.Add(new RouteViewModel(this, input, mix, r.Gain));
                mix.Bus.Connect(input.Channel, (float)r.Gain);
            }
            mix.SyncSelectedDevice();
        }

        foreach (var input in Inputs) HookInput(input);
        foreach (var mix in Mixes) HookMix(mix);

        await Task.WhenAll(Inputs.Select(StartInputAsync).Concat(Mixes.Select(StartMixAsync)));
        RoutesChanged?.Invoke();
        if (!_settings.FirstRunDone)
        {
            _settings.FirstRunDone = true;
            MarkDirty();
        }
    }

    /// <summary>Call ~30 times a second from the UI thread.</summary>
    public void Tick()
    {
        foreach (var i in Inputs) i.Tick();
        foreach (var m in Mixes) m.Tick();

        var now = DateTime.UtcNow;
        if (_saveDue is { } due && now >= due) SaveNow();
        if (_deviceRefreshDue is { } refresh && now >= refresh)
        {
            _deviceRefreshDue = null;
            _ = RecoverAfterDeviceChangeAsync();
        }
        if (now >= _nextAppPoll)
        {
            _nextAppPoll = now + AppPollInterval;
            PollApps();
        }
    }

    public void MarkDirty() => _saveDue = DateTime.UtcNow + SaveDelay;

    public void SaveNow()
    {
        _saveDue = null;
        try
        {
            _settings.Inputs = Inputs.Select(i => i.Config).ToList();
            _settings.Mixes = Mixes.Select(m =>
            {
                m.Config.Routes = m.Routes.Select(r => new RouteConfig { InputId = r.Input.Id, Gain = r.Gain }).ToList();
                return m.Config;
            }).ToList();
            _store.Save(_settings);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Couldn't save settings to {_store.FilePath}: {ex.Message}";
        }
    }

    public async ValueTask DisposeAsync()
    {
        SaveNow();
        try
        {
            await _engine.StopMonitorAsync();
            foreach (var m in Mixes) await _engine.StopMixAsync(m.Bus);
            foreach (var i in Inputs) await _engine.StopInputAsync(i.Channel);
        }
        catch (Exception)
        {
            // Exiting regardless.
        }
        await _engine.DisposeAsync();
    }

    // ---------- Inputs ----------

    public async Task<IReadOnlyList<AudioAppInfo>> GetAudioAppsAsync()
    {
        try { return await _engine.GetAudioAppsAsync(); }
        catch (Exception) { return []; }
    }

    public async Task AddInputAsync(InputConfig config)
    {
        config.ColorIndex = NextColorIndex();
        var vm = new InputViewModel(this, config);
        HookInput(vm);
        Inputs.Add(vm);
        MarkDirty();
        await StartInputAsync(vm);
    }

    /// <summary>Copy of an input's saved source, for the editor.</summary>
    public InputConfig GetInputConfig(InputViewModel vm) => new()
    {
        Id = vm.Config.Id,
        Name = vm.Config.Name,
        Kind = vm.Config.Kind,
        DeviceId = vm.Config.DeviceId,
        ProcessName = vm.Config.ProcessName,
    };

    public async Task ApplyInputEditAsync(InputViewModel vm, InputConfig config)
    {
        vm.ReplaceConfig(config);
        MarkDirty();
        await StartInputAsync(vm);
    }

    internal void RemoveInput(InputViewModel vm)
    {
        foreach (var mix in Mixes)
        {
            foreach (var route in mix.Routes.Where(r => r.Input == vm).ToList())
            {
                mix.Bus.Disconnect(vm.Channel);
                mix.Routes.Remove(route);
            }
        }
        Inputs.Remove(vm);
        _ = _engine.StopInputAsync(vm.Channel);
        MarkDirty();
        RoutesChanged?.Invoke();
    }

    internal string CaptureDeviceName(string? id)
    {
        if (DeviceIds.IsDefault(id)) return "Default microphone";
        return _captureInfo.FirstOrDefault(d => d.Id == id)?.Name ?? "Disconnected microphone";
    }

    internal async Task StartInputAsync(InputViewModel vm)
    {
        vm.NeedsRestart = false;
        try
        {
            await _engine.StartInputAsync(vm.Channel, vm.Config);
            vm.IsWaiting = false;
            vm.Status = null;
        }
        catch (AppNotRunningException ex)
        {
            vm.IsWaiting = true;
            vm.Status = $"{ex.Message}…";
        }
        catch (Exception ex)
        {
            vm.IsWaiting = false;
            vm.NeedsRestart = true;
            vm.Status = Friendly(ex);
        }
    }

    private void HookInput(InputViewModel vm) =>
        vm.Channel.Faulted += (_, ex) => _ui.Post(() =>
        {
            vm.Status = "Lost the source. Reconnecting…";
            vm.NeedsRestart = true;
            _deviceRefreshDue ??= DateTime.UtcNow + TimeSpan.FromSeconds(1);
        });

    private void PollApps()
    {
        foreach (var vm in Inputs.Where(i => i.Kind == InputKind.App))
        {
            if (vm.IsWaiting)
                _ = StartInputAsync(vm);
            else if (vm.Channel.TargetProcessId is int pid && !ProcessTree.IsAlive(pid))
                _ = StartInputAsync(vm); // app closed: drop back to "waiting"
        }
    }

    // ---------- Mixes & routes ----------

    [RelayCommand]
    public async Task AddMixAsync()
    {
        var cfg = new MixConfig { Name = $"Mix {Mixes.Count + 1}", DeviceId = DeviceIds.Default };
        var vm = new MixViewModel(this, cfg);
        HookMix(vm);
        Mixes.Add(vm);
        vm.SyncSelectedDevice();
        MarkDirty();
        await StartMixAsync(vm);
        RoutesChanged?.Invoke();
    }

    internal void RemoveMix(MixViewModel vm)
    {
        if (vm.IsListening) _ = SetListeningAsync(vm, false);
        foreach (var r in vm.Routes.ToList()) vm.Bus.Disconnect(r.Input.Channel);
        vm.Routes.Clear();
        Mixes.Remove(vm);
        _ = _engine.StopMixAsync(vm.Bus);
        MarkDirty();
        RoutesChanged?.Invoke();
    }

    public void Connect(InputViewModel input, MixViewModel mix)
    {
        if (mix.Routes.Any(r => r.Input == input)) return;
        mix.Routes.Add(new RouteViewModel(this, input, mix, 1.0));
        mix.Bus.Connect(input.Channel, 1f);
        MarkDirty();
        RoutesChanged?.Invoke();
    }

    public void Disconnect(RouteViewModel route)
    {
        route.Mix.Bus.Disconnect(route.Input.Channel);
        route.Mix.Routes.Remove(route);
        MarkDirty();
        RoutesChanged?.Invoke();
    }

    internal async Task StartMixAsync(MixViewModel vm)
    {
        vm.NeedsRestart = false;
        try
        {
            var started = await _engine.StartMixAsync(vm.Bus, vm.Config.DeviceId);
            vm.Status = null;
            if (started && vm.IsListening) await SetListeningAsync(vm, true);
        }
        catch (Exception ex)
        {
            vm.NeedsRestart = true;
            vm.Status = Friendly(ex);
        }
    }

    private void HookMix(MixViewModel vm) =>
        vm.Bus.Faulted += (_, ex) => _ui.Post(() =>
        {
            vm.Status = "Lost the output device. Reconnecting…";
            vm.NeedsRestart = true;
            _deviceRefreshDue ??= DateTime.UtcNow + TimeSpan.FromSeconds(1);
        });

    internal async Task SetListeningAsync(MixViewModel mix, bool on)
    {
        foreach (var m in Mixes) m.SetListeningState(on && m == mix);
        try
        {
            if (on) await _engine.StartMonitorAsync(mix.Bus, _settings.MonitorDeviceId);
            else await _engine.StopMonitorAsync();
        }
        catch (Exception ex)
        {
            mix.SetListeningState(false);
            ErrorMessage = Friendly(ex);
        }
    }

    internal void RaiseEditInput(InputViewModel vm) => EditInputRequested?.Invoke(vm);

    internal void RaiseRename(object vm) => RenameRequested?.Invoke(vm);

    // ---------- Devices ----------

    private void OnDeviceChanged(DeviceChange change)
    {
        if (change.Kind == DeviceChangeKind.DefaultChanged)
        {
            if (change.Flow == DataFlow.Capture) _defaultCaptureChanged = true;
            if (change.Flow == DataFlow.Render) _defaultRenderChanged = true;
        }
        // Windows fires bursts of events; coalesce.
        _deviceRefreshDue = DateTime.UtcNow + TimeSpan.FromMilliseconds(500);
    }

    private async Task RecoverAfterDeviceChangeAsync()
    {
        await RefreshDevicesAsync();
        await RefreshVbCableAsync();
        AutoAssignCable();

        bool capture = _defaultCaptureChanged, render = _defaultRenderChanged;
        _defaultCaptureChanged = _defaultRenderChanged = false;

        foreach (var i in Inputs)
        {
            if (i.NeedsRestart || (capture && i.Kind == InputKind.Device && DeviceIds.IsDefault(i.Config.DeviceId)))
                await StartInputAsync(i);
            i.RefreshSource();
        }
        foreach (var m in Mixes)
        {
            if (m.NeedsRestart || (render && m.Config.DeviceId == DeviceIds.Default))
                await StartMixAsync(m);
        }
        var listening = Mixes.FirstOrDefault(m => m.IsListening);
        if (listening is not null && render && DeviceIds.IsDefault(_settings.MonitorDeviceId))
            await SetListeningAsync(listening, true);
    }

    private async Task RefreshDevicesAsync()
    {
        IReadOnlyList<AudioDeviceInfo> outputs, inputs;
        try
        {
            outputs = await _engine.GetDevicesAsync(DataFlow.Render);
            inputs = await _engine.GetDevicesAsync(DataFlow.Capture);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Couldn't list audio devices: {ex.Message}";
            return;
        }

        Sync(OutputDevices, [new DeviceOption(DeviceIds.Default, "Windows default output"),
            .. outputs.Select(d => new DeviceOption(d.Id, d.Name, d.IsVbCable))]);
        Sync(CaptureDevices, [new DeviceOption(DeviceIds.Default, "Windows default microphone"),
            .. inputs.Select(d => new DeviceOption(d.Id, d.Name))]);
        _captureInfo.Clear();
        _captureInfo.AddRange(inputs);

        foreach (var m in Mixes) m.SyncSelectedDevice();
        _monitorDevice = OutputDevices.FirstOrDefault(d => d.Id == (_settings.MonitorDeviceId ?? DeviceIds.Default));
        OnPropertyChanged(nameof(MonitorDevice));
    }

    /// <summary>Diff-update so bound pickers keep their selection.</summary>
    private static void Sync(ObservableCollection<DeviceOption> target, List<DeviceOption> next)
    {
        for (int i = target.Count - 1; i >= 0; i--)
            if (!next.Contains(target[i])) target.RemoveAt(i);
        for (int i = 0; i < next.Count; i++)
        {
            if (i < target.Count && Equals(target[i], next[i])) continue;
            var existing = target.IndexOf(next[i]);
            if (existing >= 0) target.Move(existing, i);
            else target.Insert(i, next[i]);
        }
    }

    private async Task RefreshVbCableAsync()
    {
        if (ShowVbBusy) return;
        try
        {
            var presence = await _engine.GetVbCablePresenceAsync();
            VbCable = presence switch
            {
                VbCablePresence.Ready => VbCableState.Ready,
                VbCablePresence.Disabled => VbCableState.Disabled,
                _ => VbCable == VbCableState.RestartRequired ? VbCableState.RestartRequired : VbCableState.Missing,
            };
        }
        catch (Exception)
        {
            VbCable = VbCableState.Unknown;
        }
    }

    /// <summary>When VB-Cable appears, give it to any mix still waiting for an output.</summary>
    private void AutoAssignCable()
    {
        var cable = OutputDevices.FirstOrDefault(d => d.IsVbCable);
        if (cable is null) return;
        var waiting = Mixes.FirstOrDefault(m => m.Config.DeviceId is null);
        if (waiting is not null) waiting.SelectedDevice = cable;
    }

    // ---------- Helpers ----------

    private void CreateDefaults()
    {
        var mic = new InputConfig { Name = "Microphone", Kind = InputKind.Device, DeviceId = DeviceIds.Default, ColorIndex = 0 };
        var desktop = new InputConfig { Name = "Desktop audio", Kind = InputKind.System, ColorIndex = 1 };
        var cable = OutputDevices.FirstOrDefault(d => d.IsVbCable);
        var stream = new MixConfig
        {
            Name = "Stream mix",
            DeviceId = cable?.Id, // unassigned until VB-Cable exists: never default to speakers (feedback)
            Routes =
            [
                new RouteConfig { InputId = mic.Id, Gain = 1.0 },
                new RouteConfig { InputId = desktop.Id, Gain = 0.6 },
            ],
        };
        _settings.Inputs = [mic, desktop];
        _settings.Mixes = [stream];
    }

    private int NextColorIndex()
    {
        var used = Inputs.Select(i => i.Config.ColorIndex).ToHashSet();
        for (int i = 0; i < Palette.Colors.Length; i++)
            if (!used.Contains(i)) return i;
        return Inputs.Count;
    }

    private static string Friendly(Exception ex) => ex switch
    {
        AudioUnavailableException => ex.Message,
        UnauthorizedAccessException => "Windows blocked access. Check Privacy & security > Microphone.",
        System.Runtime.InteropServices.COMException com when (uint)com.HResult == 0x88890004 => "Device was unplugged or disabled.",
        System.Runtime.InteropServices.COMException com when (uint)com.HResult == 0x8889000A => "Device is in use exclusively by another app.",
        _ => ex.Message,
    };
}
