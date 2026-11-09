using System;
using System.Runtime.InteropServices;
using NAudio.Wave;

namespace MicSwitcher.App.Audio;

/// <summary>The few facts about a capture format that level measuring needs.</summary>
public readonly record struct SampleFormat(int Bits, bool IsFloat, int BlockAlign, int SampleRate);

/// <summary>Converts a raw capture buffer into an RMS level in dBFS.</summary>
public static class LevelMeter
{
    public const double FloorDb = -100;

    /// <summary>Describes a WASAPI format; false when it is not plain 16/24/32-bit PCM or 32-bit float.</summary>
    public static bool TryDescribe(WaveFormat format, out SampleFormat result)
    {
        result = default;

        try
        {
            if (format is WaveFormatExtensible extensible)
            {
                format = extensible.ToStandardWaveFormat();
            }
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        bool isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat;
        bool isPcm = format.Encoding == WaveFormatEncoding.Pcm;
        bool supported = (isFloat && format.BitsPerSample == 32)
            || (isPcm && (format.BitsPerSample == 16 || format.BitsPerSample == 24 || format.BitsPerSample == 32));
        if (!supported || format.BlockAlign <= 0 || format.SampleRate <= 0)
        {
            return false;
        }

        result = new SampleFormat(format.BitsPerSample, isFloat, format.BlockAlign, format.SampleRate);
        return true;
    }

    /// <summary>RMS level (dBFS) of <paramref name="count"/> bytes starting at <paramref name="offset"/>.</summary>
    public static double ComputeDb(byte[] buffer, int offset, int count, SampleFormat format)
    {
        int bytesPerSample = format.Bits / 8;
        int samples = bytesPerSample > 0 ? count / bytesPerSample : 0;
        if (samples <= 0)
        {
            return FloorDb;
        }

        ReadOnlySpan<byte> bytes = buffer.AsSpan(offset, samples * bytesPerSample);
        double sumSquares = 0;

        if (format.IsFloat)
        {
            foreach (float value in MemoryMarshal.Cast<byte, float>(bytes))
            {
                sumSquares += (double)value * value;
            }
        }
        else if (format.Bits == 16)
        {
            foreach (short value in MemoryMarshal.Cast<byte, short>(bytes))
            {
                double d = value / 32768.0;
                sumSquares += d * d;
            }
        }
        else if (format.Bits == 32)
        {
            foreach (int value in MemoryMarshal.Cast<byte, int>(bytes))
            {
                double d = value / 2147483648.0;
                sumSquares += d * d;
            }
        }
        else
        {
            for (int i = 0; i < samples; i++)
            {
                int o = i * 3;
                int value = ((bytes[o] << 8) | (bytes[o + 1] << 16) | (bytes[o + 2] << 24)) >> 8;
                double d = value / 8388608.0;
                sumSquares += d * d;
            }
        }

        double rms = Math.Sqrt(sumSquares / samples);
        return rms <= 1e-5 ? FloorDb : Math.Max(FloorDb, 20 * Math.Log10(rms));
    }
}
