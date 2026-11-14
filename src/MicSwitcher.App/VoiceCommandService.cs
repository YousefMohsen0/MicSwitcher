using System;
using System.Collections.Generic;
using System.Linq;
using System.Speech.Recognition;
using MicSwitcher.Core;

namespace MicSwitcher.App;

/// <summary>
/// Optional offline voice commands ("hey mic, switch to headset"). Uses the speech recognizer built into Windows,
/// listens on the current default microphone and only knows a short list of phrases, which keeps it accurate.
/// Nothing is sent anywhere. The recognizer is only loaded while the feature is switched on.
/// </summary>
public sealed class VoiceCommandService : IDisposable
{
    private const float MinConfidence = 0.65f;

    private readonly AppSettings _settings;
    private SpeechRecognitionEngine? _engine;

    public VoiceCommandService(AppSettings settings) => _settings = settings;

    public event Action<VoiceCommand>? CommandHeard;

    public bool IsRunning => _engine is not null;

    /// <summary>Starts (or restarts with the current mic and profile names). Returns an error text, or null on success.</summary>
    public string? Start()
    {
        Stop();
        try
        {
            RecognizerInfo? info = SpeechRecognitionEngine.InstalledRecognizers()
                .FirstOrDefault(r => r.Culture.Name.StartsWith("en", StringComparison.OrdinalIgnoreCase))
                ?? SpeechRecognitionEngine.InstalledRecognizers().FirstOrDefault();
            if (info is null)
            {
                return Loc.T("Voice_NoRecognizer");
            }

            string wake = VoiceCommandParser.Clean(_settings.WakePhrase);
            if (wake.Length == 0)
            {
                return Loc.T("Voice_NeedWake");
            }

            var phrases = new List<string>();
            foreach (string control in new[] { "pause", "resume", "unlock", "next mic" })
            {
                phrases.Add($"{wake} {control}");
            }

            foreach (MicSlot mic in _settings.Mics)
            {
                string name = VoiceCommandParser.Clean(_settings.MicName(mic.Id));
                if (name.Length > 0 && name.All(c => c < 128))
                {
                    phrases.Add($"{wake} switch to {name}");
                    phrases.Add($"{wake} use {name}");
                }
            }

            foreach (Profile profile in _settings.Profiles)
            {
                string name = VoiceCommandParser.Clean(profile.Name);
                if (name.Length > 0 && name.All(c => c < 128))
                {
                    phrases.Add($"{wake} profile {name}");
                }
            }

            var engine = new SpeechRecognitionEngine(info);
            var builder = new GrammarBuilder(new Choices(phrases.Distinct().ToArray())) { Culture = info.Culture };
            engine.LoadGrammar(new Grammar(builder));
            engine.SetInputToDefaultAudioDevice();
            engine.SpeechRecognized += OnRecognized;
            engine.SpeechRecognitionRejected += OnRejected;
            engine.RecognizeAsync(RecognizeMode.Multiple);
            _engine = engine;
            AppLog.Info(Loc.F("Log_VoiceOn", info.Culture.Name));
            return null;
        }
        catch (Exception ex)
        {
            AppLog.Info($"Voice commands could not start: {ex.Message}");
            Stop();
            return ex.Message;
        }
    }

    public void Stop()
    {
        SpeechRecognitionEngine? engine = _engine;
        _engine = null;
        if (engine is null)
        {
            return;
        }

        try
        {
            engine.SpeechRecognized -= OnRecognized;
            engine.SpeechRecognitionRejected -= OnRejected;
            engine.RecognizeAsyncCancel();
            engine.Dispose();
        }
        catch (Exception)
        {
            // Already stopped.
        }
    }

    private void OnRecognized(object? sender, SpeechRecognizedEventArgs e)
    {
        if (e.Result.Confidence < MinConfidence)
        {
            AppLog.Info(Loc.F("Log_VoiceUnsure", e.Result.Text, $"{e.Result.Confidence:P0}"));
            return;
        }

        AppLog.Info(Loc.F("Log_VoiceHeard", e.Result.Text));

        var mics = _settings.Mics.Select(m => (m.Id, _settings.MicName(m.Id))).ToList();
        var profiles = _settings.Profiles.Select(p => (p.Id, p.Name)).ToList();
        VoiceCommand? command = VoiceCommandParser.Parse(e.Result.Text, _settings.WakePhrase, mics, profiles);
        if (command is not null)
        {
            CommandHeard?.Invoke(command);
        }
    }

    private static void OnRejected(object? sender, SpeechRecognitionRejectedEventArgs e) =>
        AppLog.Info(Loc.T("Log_VoiceRejected"));

    public void Dispose() => Stop();
}
