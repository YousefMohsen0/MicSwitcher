using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using MicSwitcher.Core;

namespace MicSwitcher.App;

/// <summary>Checks GitHub for a newer installer, downloads it, verifies it and runs it.</summary>
public sealed class UpdateService
{
    public const string Repository = "YousefMohsen0/MicSwitcher";

    private readonly AppSettings _settings;
    private int _busy;

    public UpdateService(AppSettings settings) => _settings = settings;

    public static Version CurrentVersion { get; } =
        UpdateChecker.Normalize(Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0));

    /// <summary>The newest release found by the last check that is newer than this one (null = up to date).</summary>
    public ReleaseInfo? Available { get; private set; }

    /// <summary>Raised (on a background thread) when <see cref="Available"/> changes.</summary>
    public event Action<ReleaseInfo?>? AvailableChanged;

    /// <summary>Returns the newer release or null. Throws on network problems so manual checks can say so.</summary>
    public async Task<ReleaseInfo?> CheckAsync(CancellationToken token = default)
    {
        using var client = CreateClient();
        string json;
        try
        {
            json = await client.GetStringAsync(
                $"https://api.github.com/repos/{Repository}/releases?per_page=15", token).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException("No public releases were found.", ex);
        }

        var releases = UpdateChecker.ParseReleases(json, Repository);
        ReleaseInfo? newer = UpdateChecker.PickNewer(releases, CurrentVersion, _settings.IncludePrereleases);

        if (newer?.Tag != Available?.Tag)
        {
            Available = newer;
            AvailableChanged?.Invoke(newer);
        }

        return newer;
    }

    /// <summary>
    /// Downloads and verifies the installer, then starts it silently; the installer restarts MicSwitcher afterwards.
    /// Returns false (and logs why) when anything looks wrong.
    /// </summary>
    public async Task<bool> DownloadAndInstallAsync(ReleaseInfo release, CancellationToken token = default)
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1)
        {
            return false;
        }

        try
        {
            string folder = Path.Combine(Path.GetTempPath(), "MicSwitcher-update");
            Directory.CreateDirectory(folder);
            string target = Path.Combine(folder, release.InstallerName);

            using (var client = CreateClient())
            using (HttpResponseMessage response = await client.GetAsync(
                release.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                await using Stream source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                await using FileStream file = File.Create(target);
                await source.CopyToAsync(file, token).ConfigureAwait(false);
            }

            if (release.Sha256 is null)
            {
                AppLog.Info("The release has no checksum, so the download was not installed.");
                File.Delete(target);
                return false;
            }

            string actual;
            await using (FileStream check = File.OpenRead(target))
            {
                actual = Convert.ToHexString(await SHA256.HashDataAsync(check, token).ConfigureAwait(false));
            }

            if (!string.Equals(actual, release.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                AppLog.Info("The downloaded update did not match its checksum and was discarded.");
                File.Delete(target);
                return false;
            }

            AppLog.Info($"Installing MicSwitcher {release.Tag}…");
            Process.Start(new ProcessStartInfo(target, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /RELAUNCH=1")
            {
                UseShellExecute = true,
            });
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppLog.Info($"Update failed: {ex.Message}");
            return false;
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("MicSwitcher", CurrentVersion.ToString()));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }
}
