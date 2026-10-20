using System;
using System.Collections.Generic;
using System.IO;

namespace MicSwitcher.App;

/// <summary>Tiny thread-safe logger: keeps recent lines in memory for the UI and appends to a rolling file.</summary>
public static class AppLog
{
    private const int MaxLines = 200;
    private const long MaxFileBytes = 512 * 1024;

    private static readonly object Gate = new();
    private static readonly Queue<string> Recent = new();

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MicSwitcher",
        "log.txt");

    /// <summary>Raised (on the calling thread) for every new line.</summary>
    public static event Action<string>? LineAdded;

    public static IReadOnlyList<string> Snapshot()
    {
        lock (Gate)
        {
            return new List<string>(Recent);
        }
    }

    public static void Info(string message)
    {
        string line = $"{DateTime.Now:HH:mm:ss}  {message}";

        lock (Gate)
        {
            Recent.Enqueue(line);
            while (Recent.Count > MaxLines)
            {
                Recent.Dequeue();
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > MaxFileBytes)
                {
                    File.Move(FilePath, FilePath + ".old", overwrite: true);
                }

                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd} {line}{Environment.NewLine}");
            }
            catch (Exception)
            {
                // Logging must never crash the app.
            }
        }

        LineAdded?.Invoke(line);
    }
}
