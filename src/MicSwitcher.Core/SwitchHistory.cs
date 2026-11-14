using System.Text.Json;

namespace MicSwitcher.Core;

/// <summary>One change of the Windows default microphone.</summary>
/// <param name="Source">"auto", "manual", "hotkey", "voice", "profile", "startup" …</param>
public sealed record SwitchEvent(DateTime Time, string ToId, string ToName, string Reason, string Source);

/// <summary>A stretch of time during which one mic was the default.</summary>
public sealed record HistorySegment(string MicId, string MicName, DateTime Start, DateTime End)
{
    public TimeSpan Length => End - Start;
}

/// <summary>Recent switches, newest last, with a size limit. Thread-safe.</summary>
public sealed class SwitchHistory
{
    private readonly object _gate = new();
    private readonly List<SwitchEvent> _events = new();
    private readonly int _capacity;

    public SwitchHistory(int capacity = 500) => _capacity = Math.Max(1, capacity);

    public event Action<SwitchEvent>? Added;

    public void Add(SwitchEvent item)
    {
        lock (_gate)
        {
            _events.Add(item);
            if (_events.Count > _capacity)
            {
                _events.RemoveRange(0, _events.Count - _capacity);
            }
        }

        Added?.Invoke(item);
    }

    public IReadOnlyList<SwitchEvent> Snapshot()
    {
        lock (_gate)
        {
            return _events.ToList();
        }
    }

    /// <summary>The periods between <paramref name="from"/> and <paramref name="to"/> grouped by which mic was the default.</summary>
    public IReadOnlyList<HistorySegment> Segments(DateTime from, DateTime to)
    {
        var result = new List<HistorySegment>();
        IReadOnlyList<SwitchEvent> events = Snapshot();
        for (int i = 0; i < events.Count; i++)
        {
            DateTime start = events[i].Time;
            DateTime end = i + 1 < events.Count ? events[i + 1].Time : to;
            start = start < from ? from : start;
            end = end > to ? to : end;
            if (end > start)
            {
                result.Add(new HistorySegment(events[i].ToId, events[i].ToName, start, end));
            }
        }

        return result;
    }

    public static SwitchHistory Load(string path, int capacity = 500)
    {
        var history = new SwitchHistory(capacity);
        try
        {
            if (File.Exists(path))
            {
                var items = JsonSerializer.Deserialize<List<SwitchEvent>>(File.ReadAllText(path));
                foreach (SwitchEvent item in items ?? new())
                {
                    history._events.Add(item);
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // A damaged history file is not worth failing over.
        }

        return history;
    }

    public void Save(string path)
    {
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonSerializer.Serialize(Snapshot()));
        }
        catch (IOException)
        {
            // Best effort.
        }
    }
}
