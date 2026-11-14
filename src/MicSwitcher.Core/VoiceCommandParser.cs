using System.Text;

namespace MicSwitcher.Core;

public enum VoiceCommandKind
{
    SwitchToMic,
    NextMic,
    Pause,
    Resume,
    ReleaseLock,
    SwitchProfile,
}

public sealed record VoiceCommand(VoiceCommandKind Kind, string? TargetId = null);

/// <summary>Turns what the recognizer heard ("hey mic switch to headset") into a command. Pure text logic.</summary>
public static class VoiceCommandParser
{
    public static VoiceCommand? Parse(
        string? heard,
        string wakePhrase,
        IReadOnlyList<(string Id, string Name)> mics,
        IReadOnlyList<(string Id, string Name)> profiles)
    {
        string text = Clean(heard);
        string wake = Clean(wakePhrase);
        if (text.Length == 0)
        {
            return null;
        }

        if (wake.Length > 0)
        {
            int at = text.IndexOf(wake, StringComparison.Ordinal);
            if (at < 0)
            {
                return null;
            }

            text = text[(at + wake.Length)..].Trim();
        }

        if (text.Length == 0)
        {
            return null;
        }

        if (text is "pause" or "pause switching" or "stop switching" or "pause automatic switching")
            return new VoiceCommand(VoiceCommandKind.Pause);
        if (text is "resume" or "unpause" or "resume switching" or "start switching" or "resume automatic switching")
            return new VoiceCommand(VoiceCommandKind.Resume);
        if (text is "unlock" or "release" or "release lock" or "automatic" or "go automatic")
            return new VoiceCommand(VoiceCommandKind.ReleaseLock);
        if (text is "next" or "next mic" or "next microphone")
            return new VoiceCommand(VoiceCommandKind.NextMic);

        foreach (string prefix in new[] { "switch to profile ", "profile ", "use profile " })
        {
            if (text.StartsWith(prefix, StringComparison.Ordinal))
            {
                string? profile = BestMatch(text[prefix.Length..], profiles);
                return profile is null ? null : new VoiceCommand(VoiceCommandKind.SwitchProfile, profile);
            }
        }

        foreach (string prefix in new[] { "switch to ", "use ", "go to ", "change to ", "select " })
        {
            if (text.StartsWith(prefix, StringComparison.Ordinal))
            {
                string? mic = BestMatch(text[prefix.Length..].Replace(" mic", string.Empty).Replace(" microphone", string.Empty).Trim(), mics);
                return mic is null ? null : new VoiceCommand(VoiceCommandKind.SwitchToMic, mic);
            }
        }

        return null;
    }

    /// <summary>Lowercase, letters and digits only, single spaces.</summary>
    public static string Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        foreach (char c in text.ToLowerInvariant())
        {
            sb.Append(char.IsLetterOrDigit(c) ? c : ' ');
        }

        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static string? BestMatch(string spoken, IReadOnlyList<(string Id, string Name)> options)
    {
        spoken = Clean(spoken);
        if (spoken.Length == 0)
        {
            return null;
        }

        string? bestId = null;
        int bestScore = 0;
        bool tie = false;
        foreach ((string id, string name) in options)
        {
            string candidate = Clean(name).Replace(" mic", string.Empty).Replace(" microphone", string.Empty).Trim();
            int score = Score(spoken, candidate);
            if (score > bestScore)
            {
                bestScore = score;
                bestId = id;
                tie = false;
            }
            else if (score == bestScore && score > 0)
            {
                tie = true;
            }
        }

        return tie ? null : bestId;
    }

    private static int Score(string spoken, string name)
    {
        if (name.Length == 0) return 0;
        if (spoken == name) return 100;
        if (name.Contains(spoken, StringComparison.Ordinal) || spoken.Contains(name, StringComparison.Ordinal)) return 60;
        if (name.Length >= 4 && Distance(spoken, name) <= 1) return 50;
        return 0;
    }

    private static int Distance(string a, string b)
    {
        int[] previous = Enumerable.Range(0, b.Length + 1).ToArray();
        for (int i = 1; i <= a.Length; i++)
        {
            int[] current = new int[b.Length + 1];
            current[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }

            previous = current;
        }

        return previous[b.Length];
    }
}
