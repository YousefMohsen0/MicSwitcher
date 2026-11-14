namespace MicSwitcher.Core;

/// <summary>
/// Tells speech-shaped sound from other noise by looking at where the energy sits in the spectrum. Speech has most of its
/// energy between roughly 150 Hz and 4 kHz, in a few strong peaks. Hum and fans sit below that, keyboard clicks and hiss
/// are spread far above it. Pure functions, no OS code.
/// </summary>
public static class SpeechAnalyzer
{
    private const int MaxWindow = 1024;
    private const int MinWindow = 256;

    [ThreadStatic] private static double[]? _re;
    [ThreadStatic] private static double[]? _im;

    /// <summary>Share of the energy that must sit in the speech band (250 Hz – 3.8 kHz).</summary>
    public const double MinSpeechBandShare = 0.55;

    /// <summary>At most this share may sit below 150 Hz (hum, rumble).</summary>
    public const double MaxLowShare = 0.35;

    /// <summary>At most this share may sit above 4.5 kHz (hiss, clicks).</summary>
    public const double MaxHighShare = 0.30;

    /// <summary>True when a block of mono samples looks like speech. Blocks too short to judge count as speech.</summary>
    public static bool LooksLikeSpeech(ReadOnlySpan<float> samples, int sampleRate)
    {
        if (samples.Length < MinWindow || sampleRate < 8000)
        {
            return true;
        }

        int n = MinWindow;
        while (n * 2 <= samples.Length && n * 2 <= MaxWindow)
        {
            n *= 2;
        }

        double[] re = _re is { Length: >= MaxWindow } ? _re : (_re = new double[MaxWindow]);
        double[] im = _im is { Length: >= MaxWindow } ? _im : (_im = new double[MaxWindow]);

        // Hann window, taken from the middle of the block.
        int start = (samples.Length - n) / 2;
        for (int i = 0; i < n; i++)
        {
            double window = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (n - 1));
            re[i] = samples[start + i] * window;
            im[i] = 0;
        }

        Fft(re, im, n);

        double binHz = (double)sampleRate / n;
        double total = 0;
        double low = 0;
        double speech = 0;
        double high = 0;
        for (int k = 1; k < n / 2; k++)
        {
            double power = re[k] * re[k] + im[k] * im[k];
            double hz = k * binHz;
            total += power;
            if (hz < 150) low += power;
            else if (hz >= 250 && hz <= 3800) speech += power;
            else if (hz > 4500) high += power;
        }

        if (total <= 1e-12)
        {
            return false;
        }

        return speech / total >= MinSpeechBandShare && low / total <= MaxLowShare && high / total <= MaxHighShare;
    }

    private static void Fft(double[] re, double[] im, int n)
    {
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
            {
                j ^= bit;
            }

            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }

        for (int len = 2; len <= n; len <<= 1)
        {
            double angle = -2 * Math.PI / len;
            double wr = Math.Cos(angle);
            double wi = Math.Sin(angle);
            for (int i = 0; i < n; i += len)
            {
                double cr = 1;
                double ci = 0;
                for (int k = 0; k < len / 2; k++)
                {
                    int a = i + k;
                    int b = a + len / 2;
                    double tr = re[b] * cr - im[b] * ci;
                    double ti = re[b] * ci + im[b] * cr;
                    re[b] = re[a] - tr;
                    im[b] = im[a] - ti;
                    re[a] += tr;
                    im[a] += ti;
                    double next = cr * wr - ci * wi;
                    ci = cr * wi + ci * wr;
                    cr = next;
                }
            }
        }
    }
}

/// <summary>
/// Smooths per-block "looks like speech" answers: a short non-speech gap (a consonant, a breath) does not break a word.
/// </summary>
public sealed class SpeechGate
{
    private double _holdMs;

    public SpeechGate(double holdMs = 300) => HoldMs = holdMs;

    public double HoldMs { get; set; }

    public bool IsOpen => _holdMs > 0;

    public bool Process(bool looksLikeSpeech, double blockMs)
    {
        _holdMs = looksLikeSpeech ? HoldMs : Math.Max(0, _holdMs - blockMs);
        return IsOpen;
    }

    public void Reset() => _holdMs = 0;
}
