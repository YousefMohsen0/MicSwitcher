using System;
using NAudio.Wave;

namespace MicSwitcher.App.Audio;

/// <summary>Converts a raw capture buffer into an RMS level in dBFS.</summary>
public static class LevelMeter
{
    public const double FloorDb = -100;

    public static double ComputeDb(byte[] buffer, int byteCount, WaveFormat format)
    {
        if (format is WaveFormatExtensible extensible)
        {
            try
            {
                format = extensible.ToStandardWaveFormat();
            }
            catch (InvalidOperationException)
            {
                return FloorDb; // Unsupported sub-format.
            }
        }

        int bytesPerSample = format.BitsPerSample / 8;
        if (bytesPerSample <= 0 || byteCount < bytesPerSample)
        {
            return FloorDb;
        }

        int samples = byteCount / bytesPerSample;
        double sumSquares = 0;

        for (int i = 0; i < samples; i++)
        {
            int offset = i * bytesPerSample;
            double value = ReadSample(buffer, offset, format.Encoding, format.BitsPerSample);
            sumSquares += value * value;
        }

        double rms = Math.Sqrt(sumSquares / samples);
        if (rms <= 1e-5)
        {
            return FloorDb;
        }

        return Math.Max(FloorDb, 20 * Math.Log10(rms));
    }

    private static double ReadSample(byte[] buffer, int offset, WaveFormatEncoding encoding, int bits)
    {
        if (encoding == WaveFormatEncoding.IeeeFloat && bits == 32)
        {
            return BitConverter.ToSingle(buffer, offset);
        }

        return bits switch
        {
            16 => BitConverter.ToInt16(buffer, offset) / 32768.0,
            24 => ((buffer[offset] << 8 | buffer[offset + 1] << 16 | buffer[offset + 2] << 24) >> 8) / 8388608.0,
            32 => BitConverter.ToInt32(buffer, offset) / 2147483648.0,
            _ => 0,
        };
    }
}
