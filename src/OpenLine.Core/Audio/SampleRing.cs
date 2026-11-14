using NAudio.Wave;

namespace OpenLine.Core.Audio;

/// <summary>Everything inside the engine runs at 48 kHz, 32-bit float, stereo.</summary>
public static class EngineFormat
{
    public const int SampleRate = 48000;
    public const int Channels = 2;

    private static readonly Guid IeeeFloatSubtype = new("00000003-0000-0010-8000-00aa00389b71");

    public static WaveFormat Create() => WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels);

    public static int MsToSamples(int ms) => SampleRate * Channels / 1000 * ms;

    public static bool Matches(WaveFormat f)
    {
        if (f.SampleRate != SampleRate || f.Channels != Channels || f.BitsPerSample != 32) return false;
        if (f.Encoding == WaveFormatEncoding.IeeeFloat) return true;
        return f is WaveFormatExtensible ext && ext.SubFormat == IeeeFloatSubtype;
    }
}

/// <summary>
/// Single-producer / single-consumer float ring between a capture thread and a render thread.
/// Primes before playing (absorbs jitter) and trims when it grows (bounds latency from clock drift).
/// </summary>
internal sealed class SampleRing
{
    private readonly float[] _data;
    private readonly int _prime;
    private readonly int _max;
    private readonly int _trimTo;
    private readonly object _gate = new();
    private int _read;
    private int _count;
    private bool _priming = true;

    public SampleRing(int primeMs = 30, int maxMs = 150, int trimToMs = 60, int capacityMs = 500)
    {
        _data = new float[EngineFormat.MsToSamples(capacityMs)];
        _prime = EngineFormat.MsToSamples(primeMs);
        _max = EngineFormat.MsToSamples(maxMs);
        _trimTo = EngineFormat.MsToSamples(trimToMs);
    }

    public void Write(ReadOnlySpan<float> src)
    {
        if (src.Length > _data.Length) src = src[^_data.Length..];
        lock (_gate)
        {
            int free = _data.Length - _count;
            if (src.Length > free) Drop(src.Length - free);

            int write = (_read + _count) % _data.Length;
            int first = Math.Min(src.Length, _data.Length - write);
            src[..first].CopyTo(_data.AsSpan(write));
            src[first..].CopyTo(_data);
            _count += src.Length;

            if (_count > _max) Drop(_count - _trimTo);
        }
    }

    /// <summary>Returns samples copied (0 while priming). Caller zero-fills the rest.</summary>
    public int Read(Span<float> dst)
    {
        lock (_gate)
        {
            if (_priming)
            {
                if (_count < _prime) return 0;
                _priming = false;
            }

            int n = Math.Min(dst.Length, _count);
            n -= n % EngineFormat.Channels;
            int first = Math.Min(n, _data.Length - _read);
            _data.AsSpan(_read, first).CopyTo(dst);
            _data.AsSpan(0, n - first).CopyTo(dst[first..]);
            _read = (_read + n) % _data.Length;
            _count -= n;

            if (n < dst.Length) _priming = true; // underrun: rebuild the cushion
            return n;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _read = 0;
            _count = 0;
            _priming = true;
        }
    }

    private void Drop(int n)
    {
        n = Math.Min(n, _count);
        n -= n % EngineFormat.Channels;
        _read = (_read + n) % _data.Length;
        _count -= n;
    }
}

/// <summary>Lock-free "max since last read" peak holder.</summary>
internal struct PeakHolder
{
    private int _bits;

    public void Offer(float value)
    {
        int bits = BitConverter.SingleToInt32Bits(value);
        while (true)
        {
            int current = Volatile.Read(ref _bits);
            if (BitConverter.Int32BitsToSingle(current) >= value) return;
            if (Interlocked.CompareExchange(ref _bits, bits, current) == current) return;
        }
    }

    public float Take() => BitConverter.Int32BitsToSingle(Interlocked.Exchange(ref _bits, 0));
}
