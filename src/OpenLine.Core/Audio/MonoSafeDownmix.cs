namespace OpenLine.Core.Audio;

/// <summary>
/// Phase-rotated mono. Voice apps (browsers, WebRTC) often sum a stereo mic to mono, which cancels
/// anything out of phase between L and R — typically music and effects in movie or game audio — while
/// dialogue survives. Rotating one channel 90° before summing keeps in-phase AND out-of-phase content
/// at full level, and sending identical L/R also covers apps that only keep the left channel.
/// </summary>
internal sealed class MonoSafeDownmix
{
    // Olli Niemitalo's 90° IIR allpass pair: 90° ±1.7° from 20 Hz to 20 kHz at 48 kHz.
    private static readonly double[] CoefA = [0.6923878, 0.9360654322959, 0.9882295226860, 0.9987488452737];
    private static readonly double[] CoefB = [0.4021921162426, 0.8561710882420, 0.9722909545651, 0.9952884791278];
    private const float Scale = 0.70710678f; // keeps in-phase content at unity

    private readonly AllpassChain _a = new(CoefA);
    private readonly AllpassChain _b = new(CoefB);
    private double _aDelayed;

    public void Reset()
    {
        _a.Reset();
        _b.Reset();
        _aDelayed = 0;
    }

    /// <summary>In place, interleaved stereo. Writes the same mono signal to both channels.</summary>
    public void Process(Span<float> interleaved)
    {
        for (int i = 0; i + 1 < interleaved.Length; i += 2)
        {
            double a = _a.Process(interleaved[i]);
            double b = _b.Process(interleaved[i + 1]);
            float m = (float)(_aDelayed + b) * Scale; // path A carries one extra sample of delay
            _aDelayed = a;
            interleaved[i] = m;
            interleaved[i + 1] = m;
        }
    }

    private sealed class AllpassChain(double[] coefficients)
    {
        private readonly double[] _c2 = coefficients.Select(c => c * c).ToArray();
        private readonly double[] _x1 = new double[coefficients.Length];
        private readonly double[] _x2 = new double[coefficients.Length];
        private readonly double[] _y1 = new double[coefficients.Length];
        private readonly double[] _y2 = new double[coefficients.Length];

        public void Reset()
        {
            Array.Clear(_x1); Array.Clear(_x2); Array.Clear(_y1); Array.Clear(_y2);
        }

        // Each section: H(z) = (c² - z⁻²) / (1 - c² z⁻²)
        public double Process(double x)
        {
            for (int k = 0; k < _c2.Length; k++)
            {
                double y = _c2[k] * (x + _y2[k]) - _x2[k];
                _x2[k] = _x1[k]; _x1[k] = x;
                _y2[k] = _y1[k]; _y1[k] = y;
                x = y;
            }
            return x;
        }
    }
}
