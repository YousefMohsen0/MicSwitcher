using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MicSwitcher.Core;

/// <summary>A published release that has an installer attached.</summary>
public sealed record ReleaseInfo(
    Version Version,
    string Tag,
    string Name,
    bool Prerelease,
    string Notes,
    string PageUrl,
    string InstallerName,
    string InstallerUrl,
    long InstallerSize,
    string? Sha256);

/// <summary>Reads the GitHub releases list and decides whether there is a newer version. No network or Windows code.</summary>
public static partial class UpdateChecker
{
    public const string InstallerPrefix = "MicSwitcher-Setup-";

    // Only plain file names such as MicSwitcher-Setup-0.3.0.exe are accepted, so a release can never make the app
    // write outside its download folder.
    [GeneratedRegex(@"^MicSwitcher-Setup-[0-9A-Za-z][0-9A-Za-z.\-]*\.exe$")]
    private static partial Regex InstallerNamePattern();

    [GeneratedRegex(@"^[0-9a-fA-F]{64}$")]
    private static partial Regex Sha256Pattern();

    /// <summary>Parses "v0.3.0", "0.3.0", "0.3.0-beta.1" or "1.2" into a three-part version.</summary>
    public static bool TryParseVersion(string? text, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string s = text.Trim();
        if (s.StartsWith('v') || s.StartsWith('V'))
        {
            s = s[1..];
        }

        int cut = s.IndexOfAny(new[] { '-', '+', ' ' });
        if (cut >= 0)
        {
            s = s[..cut];
        }

        if (!Version.TryParse(s, out Version? parsed))
        {
            return false;
        }

        version = Normalize(parsed);
        return true;
    }

    /// <summary>Reduces a version to major.minor.build so 0.3.0 and 0.3.0.0 compare equal.</summary>
    public static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    /// <summary>
    /// Reads the JSON returned by GET /repos/{repository}/releases. Drafts, releases without a valid installer asset and
    /// assets that are not hosted in this repository's releases are skipped.
    /// </summary>
    public static IReadOnlyList<ReleaseInfo> ParseReleases(string json, string repository)
    {
        var result = new List<ReleaseInfo>();
        string urlPrefix = $"https://github.com/{repository}/releases/download/";

        using JsonDocument document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (JsonElement release in document.RootElement.EnumerateArray())
        {
            if (release.ValueKind != JsonValueKind.Object || GetBool(release, "draft"))
            {
                continue;
            }

            string tag = GetString(release, "tag_name");
            if (!TryParseVersion(tag, out Version version))
            {
                continue;
            }

            if (!release.TryGetProperty("assets", out JsonElement assets) || assets.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (JsonElement asset in assets.EnumerateArray())
            {
                if (asset.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                string name = GetString(asset, "name");
                string url = GetString(asset, "browser_download_url");
                string state = GetString(asset, "state");
                if (!InstallerNamePattern().IsMatch(name)
                    || !url.StartsWith(urlPrefix, StringComparison.Ordinal)
                    || (state.Length > 0 && state != "uploaded"))
                {
                    continue;
                }

                long size = asset.TryGetProperty("size", out JsonElement sizeElement) && sizeElement.TryGetInt64(out long s) ? s : 0;
                result.Add(new ReleaseInfo(
                    version,
                    tag,
                    GetString(release, "name"),
                    GetBool(release, "prerelease"),
                    GetString(release, "body"),
                    GetString(release, "html_url"),
                    name,
                    url,
                    size,
                    ParseDigest(GetString(asset, "digest"))));
                break; // one installer per release
            }
        }

        return result;
    }

    /// <summary>The newest release that is newer than <paramref name="current"/>, or null.</summary>
    public static ReleaseInfo? PickNewer(IEnumerable<ReleaseInfo> releases, Version current, bool includePrereleases)
    {
        Version installed = Normalize(current);
        return releases
            .Where(r => r.Version > installed && (includePrereleases || !r.Prerelease))
            .OrderByDescending(r => r.Version)
            .FirstOrDefault();
    }

    /// <summary>Turns GitHub's "sha256:abcdef…" digest into lower-case hex, or null when absent or malformed.</summary>
    public static string? ParseDigest(string? digest)
    {
        const string prefix = "sha256:";
        if (digest is null || !digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string hex = digest[prefix.Length..];
        return Sha256Pattern().IsMatch(hex) ? hex.ToLower(CultureInfo.InvariantCulture) : null;
    }

    private static string GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static bool GetBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.True;
}
