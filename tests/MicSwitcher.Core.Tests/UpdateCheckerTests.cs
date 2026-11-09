using MicSwitcher.Core;
using Xunit;

namespace MicSwitcher.Core.Tests;

public class UpdateCheckerTests
{
    private const string Repo = "YousefMohsen0/MicSwitcher";
    private const string Digest = "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static string Release(
        string tag,
        bool prerelease = false,
        bool draft = false,
        string? assetName = null,
        string? assetUrl = null,
        string? digest = Digest)
    {
        assetName ??= $"MicSwitcher-Setup-{tag.TrimStart('v')}.exe";
        assetName = assetName.Replace("\\", "\\\\"); // keep backslashes valid inside the JSON text
        assetUrl ??= $"https://github.com/{Repo}/releases/download/{tag}/{assetName}";
        string digestJson = digest is null ? "" : $", \"digest\": \"{digest}\"";
        return $$"""
            {
              "tag_name": "{{tag}}", "name": "MicSwitcher {{tag}}", "draft": {{draft.ToString().ToLowerInvariant()}},
              "prerelease": {{prerelease.ToString().ToLowerInvariant()}}, "body": "notes for {{tag}}",
              "html_url": "https://github.com/{{Repo}}/releases/tag/{{tag}}",
              "assets": [
                { "name": "{{assetName}}", "state": "uploaded", "size": 1234, "browser_download_url": "{{assetUrl}}"{{digestJson}} }
              ]
            }
            """;
    }

    private static string List(params string[] releases) => "[" + string.Join(",", releases) + "]";

    [Fact]
    public void ParsesVersions()
    {
        Assert.True(UpdateChecker.TryParseVersion("v0.3.0", out Version a));
        Assert.Equal(new Version(0, 3, 0), a);

        Assert.True(UpdateChecker.TryParseVersion("0.3.0-beta.1", out Version b));
        Assert.Equal(new Version(0, 3, 0), b);

        Assert.True(UpdateChecker.TryParseVersion("1.2", out Version c));
        Assert.Equal(new Version(1, 2, 0), c);

        Assert.True(UpdateChecker.TryParseVersion("2.0.1.7", out Version d));
        Assert.Equal(new Version(2, 0, 1), d);
    }

    [Fact]
    public void RejectsGarbageVersions()
    {
        Assert.False(UpdateChecker.TryParseVersion(null, out _));
        Assert.False(UpdateChecker.TryParseVersion("", out _));
        Assert.False(UpdateChecker.TryParseVersion("latest", out _));
        Assert.False(UpdateChecker.TryParseVersion("7", out _));
    }

    [Fact]
    public void ParsesAReleaseWithItsInstaller()
    {
        var releases = UpdateChecker.ParseReleases(List(Release("v0.3.0", prerelease: true)), Repo);

        var release = Assert.Single(releases);
        Assert.Equal(new Version(0, 3, 0), release.Version);
        Assert.True(release.Prerelease);
        Assert.Equal("MicSwitcher-Setup-0.3.0.exe", release.InstallerName);
        Assert.Equal("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", release.Sha256);
        Assert.Equal(1234, release.InstallerSize);
        Assert.Equal("notes for v0.3.0", release.Notes);
    }

    [Fact]
    public void SkipsDrafts_AndReleasesWithoutAnInstaller()
    {
        string noInstaller = """
            { "tag_name": "v0.9.0", "draft": false, "prerelease": false,
              "assets": [ { "name": "MicSwitcher.exe", "state": "uploaded", "browser_download_url": "https://github.com/YousefMohsen0/MicSwitcher/releases/download/v0.9.0/MicSwitcher.exe" } ] }
            """;

        var releases = UpdateChecker.ParseReleases(List(Release("v0.4.0", draft: true), noInstaller, Release("v0.2.0")), Repo);

        var release = Assert.Single(releases);
        Assert.Equal(new Version(0, 2, 0), release.Version);
    }

    [Fact]
    public void RejectsInstallersHostedAnywhereElse()
    {
        string evil = Release("v0.5.0", assetUrl: "https://evil.example.com/MicSwitcher-Setup-0.5.0.exe");
        string otherRepo = Release("v0.6.0", assetUrl: "https://github.com/someone/else/releases/download/v0.6.0/MicSwitcher-Setup-0.6.0.exe");

        Assert.Empty(UpdateChecker.ParseReleases(List(evil, otherRepo), Repo));
    }

    [Fact]
    public void RejectsInstallerNamesThatCouldEscapeTheDownloadFolder()
    {
        string traversal = Release("v0.5.0", assetName: "MicSwitcher-Setup-..\\..\\evil.exe");
        string slash = Release("v0.6.0", assetName: "MicSwitcher-Setup-1/../../x.exe");

        Assert.Empty(UpdateChecker.ParseReleases(List(traversal, slash), Repo));
    }

    [Fact]
    public void MissingOrMalformedDigest_IsNull()
    {
        Assert.Null(Assert.Single(UpdateChecker.ParseReleases(List(Release("v0.3.0", digest: null)), Repo)).Sha256);
        Assert.Null(Assert.Single(UpdateChecker.ParseReleases(List(Release("v0.3.0", digest: "sha256:abc")), Repo)).Sha256);
        Assert.Null(UpdateChecker.ParseDigest("md5:0123456789abcdef0123456789abcdef"));
    }

    [Fact]
    public void NotAnArray_YieldsNothing()
    {
        Assert.Empty(UpdateChecker.ParseReleases("{\"message\": \"Not Found\"}", Repo));
    }

    [Fact]
    public void PicksTheNewestRelease_NewerThanTheCurrentVersion()
    {
        var releases = UpdateChecker.ParseReleases(
            List(Release("v0.2.0"), Release("v0.4.0"), Release("v0.3.0")), Repo);

        var newer = UpdateChecker.PickNewer(releases, new Version(0, 2, 0, 0), includePrereleases: true);

        Assert.NotNull(newer);
        Assert.Equal(new Version(0, 4, 0), newer!.Version);
    }

    [Fact]
    public void NothingNewer_ReturnsNull()
    {
        var releases = UpdateChecker.ParseReleases(List(Release("v0.3.0")), Repo);

        Assert.Null(UpdateChecker.PickNewer(releases, new Version(0, 3, 0), includePrereleases: true));
        Assert.Null(UpdateChecker.PickNewer(releases, new Version(0, 3, 0, 0), includePrereleases: true));
        Assert.Null(UpdateChecker.PickNewer(releases, new Version(1, 0, 0), includePrereleases: true));
    }

    [Fact]
    public void Prereleases_AreOnlyOfferedWhenAllowed()
    {
        var releases = UpdateChecker.ParseReleases(
            List(Release("v0.5.0", prerelease: true), Release("v0.4.0")), Repo);

        Assert.Equal(new Version(0, 5, 0), UpdateChecker.PickNewer(releases, new Version(0, 3, 0), includePrereleases: true)!.Version);
        Assert.Equal(new Version(0, 4, 0), UpdateChecker.PickNewer(releases, new Version(0, 3, 0), includePrereleases: false)!.Version);
    }
}
