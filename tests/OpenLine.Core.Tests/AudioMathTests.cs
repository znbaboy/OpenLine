using OpenLine.Core.Audio;
using Xunit;

namespace OpenLine.Core.Tests;

public class SampleRingTests
{
    private static float[] Constant(int ms, float v) => Enumerable.Repeat(v, EngineFormat.MsToSamples(ms)).ToArray();

    [Fact]
    public void Holds_back_until_primed()
    {
        var ring = new SampleRing(primeMs: 30);
        var dst = new float[EngineFormat.MsToSamples(10)];
        ring.Write(Constant(20, 0.5f));
        Assert.Equal(0, ring.Read(dst));
        ring.Write(Constant(10, 0.5f));
        Assert.Equal(dst.Length, ring.Read(dst));
        Assert.All(dst, s => Assert.Equal(0.5f, s));
    }

    [Fact]
    public void Trims_when_latency_grows()
    {
        var ring = new SampleRing(primeMs: 30, maxMs: 150, trimToMs: 60);
        ring.Write(Constant(200, 0.1f));
        var dst = new float[EngineFormat.MsToSamples(500)];
        int n = ring.Read(dst);
        Assert.True(n <= EngineFormat.MsToSamples(60), $"kept {n} samples");
        Assert.Equal(0, n % EngineFormat.Channels);
    }

    [Fact]
    public void Wraps_around_without_corruption()
    {
        var ring = new SampleRing(primeMs: 0, maxMs: 400, trimToMs: 300, capacityMs: 100);
        var chunk = new float[EngineFormat.MsToSamples(30)];
        var dst = new float[chunk.Length];
        for (int round = 0; round < 20; round++)
        {
            for (int i = 0; i < chunk.Length; i++) chunk[i] = round + i * 1e-6f;
            ring.Write(chunk);
            Assert.Equal(chunk.Length, ring.Read(dst));
            Assert.Equal(chunk, dst);
        }
    }
}

public class MixBusTests
{
    private const int Ms = 10;

    private static (MixBus bus, InputChannel a, InputChannel b) Setup(float gainA, float gainB)
    {
        var bus = new MixBus(Guid.NewGuid());
        var a = new InputChannel(Guid.NewGuid());
        var b = new InputChannel(Guid.NewGuid());
        bus.Connect(a, gainA);
        bus.Connect(b, gainB);
        return (bus, a, b);
    }

    private static void Feed(InputChannel ch, float value, int ms) =>
        ch.Process(Enumerable.Repeat(value, EngineFormat.MsToSamples(ms)).ToArray());

    private static float[] Pull(MixBus bus, int ms)
    {
        var buf = new float[EngineFormat.MsToSamples(ms)];
        Assert.Equal(buf.Length, bus.Read(buf));
        return buf;
    }

    [Fact]
    public void Sums_routes_at_their_gains_after_the_ramp()
    {
        var (bus, a, b) = Setup(0.5f, 1.0f);
        Feed(a, 0.4f, 60);
        Feed(b, 0.2f, 60);
        var first = Pull(bus, Ms);
        Assert.True(Math.Abs(first[0]) < 0.05f, "first block ramps in from silence");
        var steady = Pull(bus, Ms);
        Assert.All(steady, s => Assert.Equal(0.4f, s, 3)); // 0.4*0.5 + 0.2*1.0
    }

    [Fact]
    public void Clamps_instead_of_wrapping()
    {
        var (bus, a, b) = Setup(1f, 1f);
        Feed(a, 0.9f, 60);
        Feed(b, 0.9f, 60);
        Pull(bus, Ms);
        Assert.All(Pull(bus, Ms), s => Assert.Equal(1f, s, 4));
    }

    [Fact]
    public void Muted_input_and_master_volume_apply()
    {
        var (bus, a, b) = Setup(1f, 1f);
        b.Muted = true;
        bus.Volume = 0.5f;
        Feed(a, 0.6f, 60);
        Feed(b, 0.6f, 60);
        Pull(bus, Ms);
        Assert.All(Pull(bus, Ms), s => Assert.Equal(0.3f, s, 3));
    }

    [Fact]
    public void Disconnect_stops_audio_and_releases_the_tap()
    {
        var (bus, a, _) = Setup(1f, 1f);
        bus.Disconnect(a);
        Feed(a, 0.7f, 60);
        Pull(bus, Ms);
        Assert.All(Pull(bus, Ms), s => Assert.Equal(0f, s));
    }

    [Fact]
    public void Meters_report_peaks_and_reset()
    {
        var (bus, a, _) = Setup(1f, 0f);
        a.Gain = 0.5f;
        Feed(a, 0.8f, 60);
        Assert.Equal(0.4f, a.TakePeak(), 3);
        Assert.Equal(0f, a.TakePeak());
        Pull(bus, Ms);
        Pull(bus, Ms);
        Assert.Equal(0.4f, bus.TakePeak(), 2);
    }
}
