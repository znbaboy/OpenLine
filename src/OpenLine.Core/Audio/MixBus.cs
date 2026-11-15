using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace OpenLine.Core.Audio;

public sealed class MixBus : ISampleProvider
{
    private sealed class Slot(InputChannel input, SampleRing ring, float gain)
    {
        public InputChannel Input { get; } = input;
        public SampleRing Ring { get; } = ring;
        public volatile float Gain = gain;
        public float Applied; // render thread only
    }

    private readonly object _gate = new();
    private Slot[] _slots = [];
    private float[] _scratch = new float[4096];
    private volatile float _volume = 1f;
    private volatile bool _muted;
    private float _appliedVolume;
    private PeakHolder _peak;
    private SampleRing? _monitorTap;
    private WasapiPlayer? _player;

    public MixBus(Guid id) => Id = id;

    public Guid Id { get; }

    public WaveFormat WaveFormat { get; } = EngineFormat.Create();

    public float Volume { get => _volume; set => _volume = value; }

    public bool Muted { get => _muted; set => _muted = value; }

    public bool IsRunning => _player is not null;

    /// <summary>Raised on an audio thread when playback stops unexpectedly (device unplugged etc).</summary>
    public event Action<MixBus, Exception?>? Faulted;

    public float TakePeak() => _peak.Take();

    public void Connect(InputChannel input, float gain)
    {
        lock (_gate)
        {
            var existing = _slots.FirstOrDefault(s => s.Input == input);
            if (existing is not null)
            {
                existing.Gain = gain;
                return;
            }
            var ring = new SampleRing();
            input.AddTap(ring);
            _slots = [.. _slots, new Slot(input, ring, gain)];
        }
    }

    public void Disconnect(InputChannel input)
    {
        lock (_gate)
        {
            var slot = _slots.FirstOrDefault(s => s.Input == input);
            if (slot is null) return;
            input.RemoveTap(slot.Ring);
            _slots = _slots.Where(s => s != slot).ToArray();
        }
    }

    public void SetRouteGain(InputChannel input, float gain)
    {
        var slot = Volatile.Read(ref _slots).FirstOrDefault(s => s.Input == input);
        if (slot is not null) slot.Gain = gain;
    }

    internal void SetMonitorTap(SampleRing? ring) => Volatile.Write(ref _monitorTap, ring);

    internal void Start(MMDevice device)
    {
        Stop();
        var player = new WasapiPlayerBuilder()
            .WithDevice(device)
            .WithSharedMode()
            .WithEventSync()
            .WithLatency(30)
            .WithMmcssThreadPriority("Audio")
            .Build();
        player.PlaybackStopped += OnStopped;
        player.Init(new SampleToWaveProvider(this));
        player.Play();
        _player = player;
    }

    internal void Stop()
    {
        var p = _player;
        _player = null;
        if (p is null) return;
        p.PlaybackStopped -= OnStopped;
        try { p.Stop(); } catch { }
        p.Dispose();
        foreach (var s in Volatile.Read(ref _slots)) s.Ring.Clear();
    }

    /// <summary>Render thread: pull every routed input, apply ramped gains, sum, clamp.</summary>
    public int Read(Span<float> buffer)
    {
        buffer.Clear();
        int len = buffer.Length - buffer.Length % EngineFormat.Channels;
        if (len == 0) return buffer.Length;
        if (_scratch.Length < len) _scratch = new float[len];
        var scratch = _scratch.AsSpan(0, len);
        int frames = len / EngineFormat.Channels;

        foreach (var slot in Volatile.Read(ref _slots))
        {
            int n = slot.Ring.Read(scratch);
            float target = slot.Input.Muted ? 0f : slot.Input.Gain * slot.Gain;
            float g = slot.Applied;
            if (n == 0 && g == 0f && target == 0f) continue;
            if (n < len) scratch[n..].Clear();

            float step = (target - g) / frames; // ramp over one buffer: no zipper noise
            for (int i = 0; i < len; i += 2)
            {
                g += step;
                buffer[i] += scratch[i] * g;
                buffer[i + 1] += scratch[i + 1] * g;
            }
            slot.Applied = target;
        }

        float mTarget = _muted ? 0f : _volume;
        float m = _appliedVolume;
        float mStep = (mTarget - m) / frames;
        float peak = 0f;
        for (int i = 0; i < len; i += 2)
        {
            m += mStep;
            float l = Math.Clamp(buffer[i] * m, -1f, 1f);
            float r = Math.Clamp(buffer[i + 1] * m, -1f, 1f);
            buffer[i] = l;
            buffer[i + 1] = r;
            peak = Math.Max(peak, Math.Max(Math.Abs(l), Math.Abs(r)));
        }
        _appliedVolume = mTarget;
        _peak.Offer(peak);

        Volatile.Read(ref _monitorTap)?.Write(buffer[..len]);
        return buffer.Length;
    }

    private void OnStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is null) return;
        Faulted?.Invoke(this, e.Exception);
    }
}

/// <summary>Plays one mix on the "Listen" device so you can hear exactly what that mix sends.</summary>
internal sealed class MonitorOutput : ISampleProvider
{
    private readonly SampleRing _ring = new(primeMs: 30, maxMs: 120, trimToMs: 50);
    private WasapiPlayer? _player;
    private MixBus? _source;

    public WaveFormat WaveFormat { get; } = EngineFormat.Create();

    public Guid? SourceId => _source?.Id;

    public void Start(MixBus source, MMDevice device)
    {
        Stop();
        _ring.Clear();
        var player = new WasapiPlayerBuilder()
            .WithDevice(device)
            .WithSharedMode()
            .WithEventSync()
            .WithLatency(30)
            .Build();
        player.Init(new SampleToWaveProvider(this));
        source.SetMonitorTap(_ring);
        player.Play();
        _player = player;
        _source = source;
    }

    public void Stop()
    {
        _source?.SetMonitorTap(null);
        _source = null;
        var p = _player;
        _player = null;
        if (p is null) return;
        try { p.Stop(); } catch { }
        p.Dispose();
    }

    public int Read(Span<float> buffer)
    {
        int n = _ring.Read(buffer);
        if (n < buffer.Length) buffer[n..].Clear();
        return buffer.Length;
    }
}
