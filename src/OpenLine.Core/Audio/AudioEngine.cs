using System.Threading.Channels;
using NAudio.CoreAudioApi;
using OpenLine.Core.Settings;

namespace OpenLine.Core.Audio;

public enum DeviceChangeKind { Added, Removed, StateChanged, DefaultChanged }

public sealed record DeviceChange(DeviceChangeKind Kind, string DeviceId, DataFlow? Flow);

/// <summary>
/// All WASAPI / COM work happens on one MTA worker, serially, so starts and stops never race
/// and the UI thread never blocks on audio drivers.
/// </summary>
public sealed class AudioEngine : IAsyncDisposable
{
    private readonly Channel<Func<Task>> _work = Channel.CreateUnbounded<Func<Task>>(new() { SingleReader = true });
    private readonly Task _pump;
    private readonly MonitorOutput _monitor = new();
    private MMDeviceEnumerator? _enumerator;
    private MMDeviceNotificationClient? _notifications;
    private DeviceCatalog? _devices;

    public AudioEngine()
    {
        _pump = Task.Run(PumpAsync);
        _ = Invoke(() =>
        {
            _enumerator = new MMDeviceEnumerator();
            _devices = new DeviceCatalog(_enumerator);
            _notifications = _enumerator.CreateNotificationClient(useSynchronizationContext: false);
            _notifications.DeviceAdded += (_, e) => Raise(new(DeviceChangeKind.Added, e.DeviceId, null));
            _notifications.DeviceRemoved += (_, e) => Raise(new(DeviceChangeKind.Removed, e.DeviceId, null));
            _notifications.DeviceStateChanged += (_, e) => Raise(new(DeviceChangeKind.StateChanged, e.DeviceId, null));
            _notifications.DefaultDeviceChanged += (_, e) =>
            {
                if (e.Role == Role.Console) Raise(new(DeviceChangeKind.DefaultChanged, e.DeviceId, e.Flow));
            };
            return true;
        });
    }

    /// <summary>Raised on a Windows audio thread. Handlers must only marshal and return.</summary>
    public event Action<DeviceChange>? DeviceChanged;

    private DeviceCatalog Devices => _devices ?? throw new InvalidOperationException("Engine not ready");

    private MMDeviceEnumerator Enumerator => _enumerator ?? throw new InvalidOperationException("Engine not ready");

    public Task<IReadOnlyList<AudioDeviceInfo>> GetDevicesAsync(DataFlow flow) => Invoke(() => Devices.List(flow));

    public Task<IReadOnlyList<AudioAppInfo>> GetAudioAppsAsync() => Invoke(() => AppCatalog.List(Enumerator));

    public Task<VbCablePresence> GetVbCablePresenceAsync() => Invoke(() => Devices.GetVbCablePresence());

    public Task StartInputAsync(InputChannel channel, InputConfig config) =>
        InvokeAsync(async () =>
        {
            await channel.StartAsync(config, Devices);
            return true;
        });

    public Task StopInputAsync(InputChannel channel) => Invoke(() => { channel.Stop(); return true; });

    /// <summary>Starts (or restarts) a mix on a device. Returns false when there's nothing to play to.</summary>
    public Task<bool> StartMixAsync(MixBus mix, string? deviceId) =>
        Invoke(() =>
        {
            mix.Stop();
            if (deviceId is null) return false;
            var device = Devices.Resolve(DataFlow.Render, deviceId)
                         ?? throw new AudioUnavailableException("Output device not found. Plug it in or pick another one.");
            mix.Start(device);
            return true;
        });

    public Task StopMixAsync(MixBus mix) => Invoke(() => { mix.Stop(); return true; });

    public Task StartMonitorAsync(MixBus mix, string? deviceId) =>
        Invoke(() =>
        {
            var device = Devices.Resolve(DataFlow.Render, deviceId)
                         ?? throw new AudioUnavailableException("Listen device not found. Pick another one in Settings.");
            _monitor.Start(mix, device);
            return true;
        });

    public Task StopMonitorAsync() => Invoke(() => { _monitor.Stop(); return true; });

    public async ValueTask DisposeAsync()
    {
        try
        {
            await Invoke(() =>
            {
                _monitor.Stop();
                _notifications?.Dispose();
                _enumerator?.Dispose();
                return true;
            }).WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (Exception)
        {
            // Shutting down anyway.
        }
        _work.Writer.TryComplete();
    }

    private void Raise(DeviceChange change)
    {
        try { DeviceChanged?.Invoke(change); } catch { }
    }

    private Task<T> Invoke<T>(Func<T> func) => InvokeAsync(() => Task.FromResult(func()));

    private Task<T> InvokeAsync<T>(Func<Task<T>> func)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_work.Writer.TryWrite(async () =>
            {
                try { tcs.SetResult(await func()); }
                catch (Exception ex) { tcs.SetException(ex); }
            }))
        {
            tcs.SetException(new ObjectDisposedException(nameof(AudioEngine)));
        }
        return tcs.Task;
    }

    private async Task PumpAsync()
    {
        await foreach (var item in _work.Reader.ReadAllAsync())
        {
            try { await item(); }
            catch { /* surfaced through the item's TaskCompletionSource */ }
        }
    }
}
