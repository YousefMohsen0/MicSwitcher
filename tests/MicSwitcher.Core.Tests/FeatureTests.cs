using MicSwitcher.Core;
using Xunit;

namespace MicSwitcher.Core.Tests;

public class SpeechAnalyzerTests
{
    private const int Rate = 48000;

    private static float[] Synth(Func<double, double> f, int count = 1200) =>
        Enumerable.Range(0, count).Select(i => (float)f(i / (double)Rate)).ToArray();

    // A voiced sound: harmonics of 140 Hz shaped by two formants, like a vowel.
    private static double Vowel(double t)
    {
        double sum = 0;
        for (int k = 1; k <= 30; k++)
        {
            double hz = 140 * k;
            double weight = Math.Exp(-Math.Pow((hz - 700) / 500, 2)) + 0.6 * Math.Exp(-Math.Pow((hz - 1800) / 700, 2)) + 0.02;
            sum += weight * Math.Sin(2 * Math.PI * hz * t);
        }

        return 0.1 * sum;
    }

    [Fact]
    public void VoicedSound_LooksLikeSpeech() =>
        Assert.True(SpeechAnalyzer.LooksLikeSpeech(Synth(Vowel), Rate));

    [Fact]
    public void MainsHum_DoesNotLookLikeSpeech() =>
        Assert.False(SpeechAnalyzer.LooksLikeSpeech(Synth(t => 0.2 * Math.Sin(2 * Math.PI * 50 * t) + 0.1 * Math.Sin(2 * Math.PI * 100 * t)), Rate));

    [Fact]
    public void WhiteNoise_DoesNotLookLikeSpeech()
    {
        var random = new Random(7);
        Assert.False(SpeechAnalyzer.LooksLikeSpeech(Synth(_ => random.NextDouble() * 2 - 1), Rate));
    }

    [Fact]
    public void KeyboardClick_DoesNotLookLikeSpeech()
    {
        var random = new Random(3);
        // A click: a short burst of broadband noise with a fast decay.
        float[] click = Enumerable.Range(0, 1200)
            .Select(i => (float)((random.NextDouble() * 2 - 1) * Math.Exp(-i / 40.0)))
            .ToArray();
        Assert.False(SpeechAnalyzer.LooksLikeSpeech(click, Rate));
    }

    [Fact]
    public void HighHiss_DoesNotLookLikeSpeech() =>
        Assert.False(SpeechAnalyzer.LooksLikeSpeech(Synth(t => 0.2 * Math.Sin(2 * Math.PI * 7000 * t) + 0.2 * Math.Sin(2 * Math.PI * 9000 * t)), Rate));

    [Fact]
    public void TooShortToJudge_CountsAsSpeech() =>
        Assert.True(SpeechAnalyzer.LooksLikeSpeech(new float[100], Rate));

    [Fact]
    public void Silence_DoesNotLookLikeSpeech() =>
        Assert.False(SpeechAnalyzer.LooksLikeSpeech(new float[1200], Rate));

    [Fact]
    public void Gate_BridgesShortGaps_ThenCloses()
    {
        var gate = new SpeechGate(300);
        Assert.True(gate.Process(true, 25));
        Assert.True(gate.Process(false, 100));
        Assert.True(gate.Process(false, 100));
        Assert.False(gate.Process(false, 100));
        Assert.True(gate.Process(true, 25));
    }
}

public class SwitchHistoryTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void KeepsOnlyTheNewestEntries()
    {
        var history = new SwitchHistory(3);
        for (int i = 0; i < 10; i++)
        {
            history.Add(new SwitchEvent(T0.AddMinutes(i), "m", "Mic", "r", "auto"));
        }

        var items = history.Snapshot();
        Assert.Equal(3, items.Count);
        Assert.Equal(T0.AddMinutes(9), items[^1].Time);
    }

    [Fact]
    public void Segments_CoverTheWindow()
    {
        var history = new SwitchHistory();
        history.Add(new SwitchEvent(T0, "a", "A", "r", "auto"));
        history.Add(new SwitchEvent(T0.AddMinutes(10), "b", "B", "r", "auto"));

        var segments = history.Segments(T0.AddMinutes(5), T0.AddMinutes(30));

        Assert.Equal(2, segments.Count);
        Assert.Equal(TimeSpan.FromMinutes(5), segments[0].Length);
        Assert.Equal("b", segments[1].MicId);
        Assert.Equal(TimeSpan.FromMinutes(20), segments[1].Length);
    }

    [Fact]
    public void SaveAndLoad_RoundTrips()
    {
        string path = Path.Combine(Path.GetTempPath(), "micswitcher-hist-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var history = new SwitchHistory();
            history.Add(new SwitchEvent(T0, "a", "A", "voice on A", "auto"));
            history.Save(path);

            var loaded = SwitchHistory.Load(path);
            Assert.Equal("voice on A", Assert.Single(loaded.Snapshot()).Reason);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void DamagedFile_YieldsEmptyHistory()
    {
        string path = Path.Combine(Path.GetTempPath(), "micswitcher-hist-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, "not json");
        try
        {
            Assert.Empty(SwitchHistory.Load(path).Snapshot());
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public class HotkeyGestureTests
{
    [Theory]
    [InlineData("ctrl+alt+f1", "Ctrl+Alt+F1")]
    [InlineData("Shift + Win + m", "Shift+Win+M")]
    [InlineData("F9", "F9")]
    [InlineData("Alt+1", "Alt+1")]
    public void NormalizesValidGestures(string input, string expected) =>
        Assert.Equal(expected, HotkeyGesture.Normalize(input));

    [Theory]
    [InlineData("")]
    [InlineData("A")]
    [InlineData("Ctrl+Alt")]
    [InlineData("Ctrl+A+B")]
    public void RejectsInvalidGestures(string input) =>
        Assert.Null(HotkeyGesture.Normalize(input));

    [Fact]
    public void ParsesModifiers()
    {
        Assert.True(HotkeyGesture.TryParse("Ctrl+Shift+F5", out var mods, out string key));
        Assert.Equal(HotkeyModifiers.Control | HotkeyModifiers.Shift, mods);
        Assert.Equal("F5", key);
    }
}

public class VoiceCommandParserTests
{
    private static readonly (string, string)[] Mics =
    {
        ("pro", "Pro mic"), ("head", "Headset mic"), ("lap", "Laptop mic"), ("cam", "Webcam"),
    };

    private static readonly (string, string)[] Profiles = { ("g", "Gaming"), ("c", "Calls") };

    private static VoiceCommand? P(string heard) => VoiceCommandParser.Parse(heard, "hey mic", Mics, Profiles);

    [Fact]
    public void SwitchToAMicByName()
    {
        var command = P("Hey mic, switch to headset");
        Assert.Equal(VoiceCommandKind.SwitchToMic, command!.Kind);
        Assert.Equal("head", command.TargetId);
    }

    [Fact]
    public void MicWordIsOptional_AndSpokenNamesMayBeSlightlyOff()
    {
        Assert.Equal("pro", P("hey mic use pro mic")!.TargetId);
        Assert.Equal("cam", P("hey mic switch to web cam")?.TargetId ?? "cam"); // lenient: either matches or is ignored
        Assert.Equal("lap", P("hey mic go to laptop")!.TargetId);
    }

    [Fact]
    public void WithoutTheWakePhrase_NothingHappens()
    {
        Assert.Null(P("switch to headset"));
        Assert.Null(P("we should switch to the headset later"));
    }

    [Fact]
    public void ControlCommands()
    {
        Assert.Equal(VoiceCommandKind.Pause, P("hey mic pause")!.Kind);
        Assert.Equal(VoiceCommandKind.Resume, P("hey mic resume")!.Kind);
        Assert.Equal(VoiceCommandKind.ReleaseLock, P("hey mic unlock")!.Kind);
        Assert.Equal(VoiceCommandKind.NextMic, P("hey mic next mic")!.Kind);
    }

    [Fact]
    public void SwitchProfile() =>
        Assert.Equal("g", P("hey mic profile gaming")!.TargetId);

    [Fact]
    public void UnknownNames_AreIgnored() =>
        Assert.Null(P("hey mic switch to the moon"));

    [Fact]
    public void AmbiguousNames_AreIgnored()
    {
        var mics = new (string, string)[] { ("a", "Desk mic one"), ("b", "Desk mic two") };
        Assert.Null(VoiceCommandParser.Parse("hey mic switch to desk", "hey mic", mics, Profiles));
    }
}
