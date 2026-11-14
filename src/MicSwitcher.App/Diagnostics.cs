using System;
using System.Linq;
using System.Reflection;
using System.Security.Principal;
using System.Text;
using MicSwitcher.App.Audio;
using MicSwitcher.Core;

namespace MicSwitcher.App;

/// <summary>Builds a text report users can attach to a bug report. Device ids are shortened and file paths left out.</summary>
public static class Diagnostics
{
    public static string Build(AppSettings settings, AudioDeviceService devices, SwitchController controller)
    {
        var sb = new StringBuilder();
        sb.AppendLine("MicSwitcher diagnostics");
        sb.AppendLine($"Version: {UpdateService.CurrentVersion}");
        sb.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Windows: {Environment.OSVersion.VersionString}, .NET {Environment.Version}");
        sb.AppendLine($"Administrator: {new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator)}");
        sb.AppendLine($"Language: {settings.Language} (using {Loc.Language.Code}), theme: {settings.Theme}");
        sb.AppendLine();

        Profile profile = settings.ActiveProfile;
        sb.AppendLine($"Active profile: {profile.Name} (mode {profile.Mode}, silence {profile.SilenceTimeoutSeconds}s, " +
                      $"return {profile.ReturnDelayMs}ms, bleed {profile.IgnoreBleed}, smart voice {profile.SmartVoice})");
        sb.AppendLine($"Paused: {settings.Paused}, locked: {controller.LockedMicId is not null}, " +
                      $"communications role: {settings.SwitchCommunicationsRole}, startup: {settings.StartWithWindows}");
        sb.AppendLine($"Playback level now: {controller.PlaybackDb:0.#} dBFS");
        sb.AppendLine();

        sb.AppendLine("Managed microphones (best first):");
        ControllerStatus? status = controller.LastStatus;
        for (int i = 0; i < settings.Mics.Count; i++)
        {
            MicSlot mic = settings.Mics[i];
            MicStatus? state = status?.Find(mic.Id);
            sb.AppendLine($"  {i + 1}. {mic.DisplayName(i)} | device: {mic.DeviceName ?? "(none)"} [{Short(mic.DeviceId)}] | " +
                          $"present: {state?.Present} | fallback: {mic.Fallback} | threshold {mic.VoiceThresholdDb:0.#} dB | " +
                          $"bleed {mic.BleedCouplingDb?.ToString("0.#") ?? "-"} dB | output: {mic.OutputDeviceName ?? "-"} {mic.OutputVolumePercent?.ToString() ?? "-"}%");
        }

        sb.AppendLine();
        sb.AppendLine("Hotkeys: " + (settings.Hotkeys.Count == 0 ? "none" : string.Join(", ", settings.Hotkeys.Select(h => $"{h.Gesture}={h.Action}"))));
        sb.AppendLine($"Voice commands: {settings.VoiceCommandsEnabled}");
        sb.AppendLine();

        try
        {
            sb.AppendLine("Recording devices Windows reports:");
            foreach (CaptureDevice d in devices.GetActiveCaptureDevices())
            {
                sb.AppendLine($"  {d.Name} [{Short(d.Id)}]");
            }

            sb.AppendLine("Playback devices Windows reports:");
            foreach (CaptureDevice d in devices.GetActiveRenderDevices())
            {
                sb.AppendLine($"  {d.Name} [{Short(d.Id)}]");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine("  (could not list devices: " + ex.Message + ")");
        }

        sb.AppendLine();
        sb.AppendLine("Recent switches:");
        foreach (SwitchEvent e in controller.History.Snapshot().TakeLast(15))
        {
            sb.AppendLine($"  {e.Time.ToLocalTime():HH:mm:ss}  {e.ToName}  ({e.Source}) {e.Reason}");
        }

        sb.AppendLine();
        sb.AppendLine("Recent log:");
        foreach (string line in AppLog.Snapshot().TakeLast(80))
        {
            sb.AppendLine("  " + line);
        }

        return sb.ToString();
    }

    private static string Short(string? id) => string.IsNullOrEmpty(id) ? "-" : "…" + id[^Math.Min(8, id.Length)..];
}
