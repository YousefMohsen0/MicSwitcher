namespace MicSwitcher.Core;

/// <summary>
/// Remembers the loudest recent level of whatever the PC is playing (calls, music…), so a microphone's reading can be
/// compared with what the speakers or headphones were doing a moment ago.
/// </summary>
/// <remarks>
/// Levels are kept in short time buckets so the "recent maximum" is exact without storing every block. Time is passed
/// in by the caller (milliseconds from any monotonic clock) so the class is easy to test.
/// </remarks>
public sealed class PlaybackTracker
{
    public const double SilenceDb = -100;

    private const int BucketMs = 50;

    private readonly int _bucketCount;
    private readonly double[] _max;
    private readonly long[] _bucketId;

    /// <param name="holdMs">How long a level is remembered. Covers the delay between the sound being played and the mic hearing it.</param>
    public PlaybackTracker(int holdMs = 400)
    {
        _bucketCount = Math.Max(2, (holdMs + BucketMs - 1) / BucketMs);
        _max = new double[_bucketCount];
        _bucketId = new long[_bucketCount];
        Array.Fill(_max, SilenceDb);
        Array.Fill(_bucketId, long.MinValue);
    }

    /// <summary>Records one measured playback level (dBFS, already adjusted for the output volume).</summary>
    public void Observe(double levelDb, long nowMs)
    {
        long id = nowMs / BucketMs;
        int index = (int)(id % _bucketCount);
        if (_bucketId[index] != id)
        {
            _bucketId[index] = id;
            _max[index] = SilenceDb;
        }

        if (levelDb > _max[index])
        {
            _max[index] = levelDb;
        }
    }

    /// <summary>The loudest level seen within the hold time, or <see cref="SilenceDb"/> when nothing was playing.</summary>
    public double RecentLevelDb(long nowMs)
    {
        long nowId = nowMs / BucketMs;
        double best = SilenceDb;
        for (int i = 0; i < _bucketCount; i++)
        {
            long id = _bucketId[i];
            if (id > nowId - _bucketCount && id <= nowId && _max[i] > best)
            {
                best = _max[i];
            }
        }

        return best;
    }
}
