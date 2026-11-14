using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using OpenLine.Core.Settings;

namespace OpenLine.Core.Audio;

public sealed class InputChannel
{
    private readonly object _tapGate = new();
    private SampleRing[] _taps = [];
    private WasapiRecorder? _recorder;
    private PeakHolder _peak;
    private float[] _silence = [];
    private volatile float _gain = 1f;
    private volatile bool _muted;

    public InputChannel(Guid id) => Id = id;

    public Guid Id { get; }

    public float Gain { get => _gain; set => _gain = value; }

    public bool Muted { get => _muted; set => _muted = value; }

    public bool IsRunning => _recorder is not null;

    /// <summary>Process being captured (Kind = App), so the UI can re-attach when it restarts.</summary>
    public int? TargetProcessId { get; private set; }

    /// <summary>Raised on an audio thread when capture stops unexpectedly.</summary>
    public event Action<InputChannel, Exception?>? Faulted;

    public float TakePeak() => _peak.Take();

    internal void AddTap(SampleRing ring)
    {
        lock (_tapGate) _taps = [.. _taps, ring];
    }

    internal void RemoveTap(SampleRing ring)
    {
        lock (_tapGate) _taps = _taps.Where(t => t != ring).ToArray();
    }

    internal async Task StartAsync(InputConfig config, DeviceCatalog devices)
    {
        Stop();

        var builder = new WasapiRecorderBuilder()
            .WithSharedMode()
            .WithEventSync()
            .WithBufferLength(20)
            .WithFormat(EngineFormat.Create())
            .WithMmcssThreadPriority("Audio");

        WasapiRecorder recorder;
        switch (config.Kind)
        {
            case InputKind.Device:
            {
                var device = devices.Resolve(DataFlow.Capture, config.DeviceId)
                             ?? throw new AudioUnavailableException("Microphone not found. Plug it in or pick another one.");
                recorder = builder.WithDevice(device).Build();
                TargetProcessId = null;
                break;
            }
            case InputKind.App:
            {
                var name = config.ProcessName ?? throw new AudioUnavailableException("No app selected.");
                var pid = AppCatalog.ResolveRootProcessId(name)
                          ?? throw new AppNotRunningException($"Waiting for {name} to start");
                recorder = await builder.WithProcessLoopback((uint)pid, ProcessLoopbackMode.IncludeTargetProcessTree).BuildAsync();
                TargetProcessId = pid;
                break;
            }
            case InputKind.System:
            {
                // Everything except our own process tree: no feedback when a mix plays on the same device.
                recorder = await builder.WithProcessLoopback((uint)Environment.ProcessId, ProcessLoopbackMode.ExcludeTargetProcessTree).BuildAsync();
                TargetProcessId = null;
                break;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(config));
        }

        if (!EngineFormat.Matches(recorder.WaveFormat))
        {
            recorder.Dispose();
            throw new AudioUnavailableException($"Unsupported capture format: {recorder.WaveFormat}");
        }

        recorder.DataAvailable += OnData;
        recorder.RecordingStopped += OnStopped;
        recorder.StartRecording();
        _recorder = recorder;
    }

    internal void Stop()
    {
        var r = _recorder;
        _recorder = null;
        if (r is null) return;
        r.RecordingStopped -= OnStopped;
        r.DataAvailable -= OnData;
        try { r.StopRecording(); } catch { }
        r.Dispose();
        TargetProcessId = null;
    }

    private void OnData(ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags, long devicePosition, long qpcPosition)
    {
        ReadOnlySpan<float> samples = MemoryMarshal.Cast<byte, float>(buffer);
        if ((flags & AudioClientBufferFlags.Silent) != 0)
        {
            if (_silence.Length < samples.Length) _silence = new float[samples.Length];
            samples = _silence.AsSpan(0, samples.Length);
        }

        Process(samples);
    }

    /// <summary>Meter + fan-out. Separate from the WASAPI callback so it can be unit-tested.</summary>
    internal void Process(ReadOnlySpan<float> samples)
    {
        float peak = 0f;
        foreach (var s in samples)
        {
            var a = Math.Abs(s);
            if (a > peak) peak = a;
        }
        _peak.Offer(_muted ? 0f : peak * _gain);

        var taps = Volatile.Read(ref _taps);
        foreach (var t in taps) t.Write(samples);
    }

    private void OnStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is null) return; // we stopped it
        Faulted?.Invoke(this, e.Exception);
    }
}

public class AudioUnavailableException(string message) : Exception(message);

public sealed class AppNotRunningException(string message) : AudioUnavailableException(message);
