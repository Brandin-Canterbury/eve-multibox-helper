using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using EveMultibox.Shell;

namespace EveMultibox.Tests;

public sealed class UpdaterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "EveMultiboxTests", Guid.NewGuid().ToString("N"));

    public UpdaterTests() => Directory.CreateDirectory(_dir);

    private static string ReleaseJson(string tag, string? assetName = "EveMultibox.exe", string? url = null, bool prerelease = false,
        long size = 71_791_986, string? digest = "sha256:7F24337999DA90CA6476B3CF8548485AFB2708A76DA2803145DEF01508217CA9") =>
        JsonSerializer.Serialize(new
        {
            tag_name = tag,
            html_url = $"https://github.com/{Updater.Repository}/releases/tag/{tag}",
            body = "**Full Changelog**: …",
            draft = false,
            prerelease,
            assets = assetName is null ? Array.Empty<object>() : new object[]
            {
                new { name = assetName, size, digest, browser_download_url = url ?? $"https://github.com/{Updater.Repository}/releases/download/{tag}/{assetName}" },
            },
        });

    [Theory]
    [InlineData("v0.3.4", "0.3.4")]
    [InlineData("0.3.12", "0.3.12")]
    [InlineData("V1.2.3", "1.2.3")]
    public void ParsesReleaseTags(string tag, string expected) => Assert.Equal(Version.Parse(expected), Updater.ParseTag(tag));

    [Theory]
    [InlineData("nightly")]
    [InlineData("")]
    public void IgnoresNonVersionTags(string tag) => Assert.Null(Updater.ParseTag(tag));

    [Fact]
    public void OffersANewerRelease()
    {
        var update = Updater.Evaluate(ReleaseJson("v0.3.5"), new Version(0, 3, 4))!;
        Assert.Equal(new Version(0, 3, 5), update.Version);
        Assert.Equal(71_791_986, update.Size);
        Assert.Equal("7f24337999da90ca6476b3cf8548485afb2708a76da2803145def01508217ca9", update.Sha256);
    }

    [Theory]
    [InlineData("v0.3.4")]   // Same version.
    [InlineData("v0.3.3")]   // Older.
    [InlineData("v0.2.99")]  // Older minor, despite the bigger patch number.
    public void IgnoresSameOrOlderReleases(string tag) => Assert.Null(Updater.Evaluate(ReleaseJson(tag), new Version(0, 3, 4)));

    [Fact]
    public void NumericVersionComparisonNotText() =>
        Assert.NotNull(Updater.Evaluate(ReleaseJson("v0.3.10"), new Version(0, 3, 9)));

    [Fact]
    public void IgnoresPrereleasesAndReleasesWithoutTheExe()
    {
        Assert.Null(Updater.Evaluate(ReleaseJson("v0.4.0", prerelease: true), new Version(0, 3, 4)));
        Assert.Null(Updater.Evaluate(ReleaseJson("v0.4.0", assetName: null), new Version(0, 3, 4)));
        Assert.Null(Updater.Evaluate(ReleaseJson("v0.4.0", assetName: "Other.zip"), new Version(0, 3, 4)));
    }

    [Theory]
    [InlineData("http://github.com/Brandin-Canterbury/eve-multibox-helper/releases/download/v1/EveMultibox.exe")]  // Not HTTPS.
    [InlineData("https://evil.example.com/Brandin-Canterbury/eve-multibox-helper/releases/download/v1/EveMultibox.exe")]
    [InlineData("https://github.com/someone-else/eve-multibox-helper/releases/download/v1/EveMultibox.exe")]
    public void OnlyDownloadsThisRepositorysReleaseFiles(string url)
    {
        Assert.False(Updater.IsTrustedDownload(url));
        Assert.Null(Updater.Evaluate(ReleaseJson("v9.0.0", url: url), new Version(0, 3, 4)));
    }

    private UpdateInfo Info(string file, string? sha = null, long? size = null) =>
        new(new Version(0, 3, 5), "v0.3.5", "https://github.com/x", size ?? new FileInfo(file).Length, sha, "https://github.com/x", "");

    [Fact]
    public void VerifyAcceptsTheExactFile()
    {
        string file = Path.Combine(_dir, "new.exe");
        File.WriteAllBytes(file, new byte[] { 1, 2, 3, 4 });
        string sha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant();
        Updater.Verify(file, Info(file, sha));
        Assert.True(File.Exists(file));
    }

    [Fact]
    public void VerifyRejectsAndDeletesATamperedFile()
    {
        string file = Path.Combine(_dir, "new.exe");
        File.WriteAllBytes(file, new byte[] { 1, 2, 3, 4 });
        Assert.Throws<InvalidDataException>(() => Updater.Verify(file, Info(file, sha: new string('0', 64))));
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void VerifyRejectsAWrongSize()
    {
        string file = Path.Combine(_dir, "new.exe");
        File.WriteAllBytes(file, new byte[] { 1, 2, 3 });
        Assert.Throws<InvalidDataException>(() => Updater.Verify(file, Info(file, size: 99)));
    }

    [Fact]
    public void SwapPutsTheNewExeInPlaceAndKeepsTheOldOneAside()
    {
        string current = Path.Combine(_dir, "EveMultibox.exe");
        string downloaded = Path.Combine(_dir, "download.exe");
        File.WriteAllText(current, "old");
        File.WriteAllText(downloaded, "new");

        Updater.Swap(current, downloaded);

        Assert.Equal("new", File.ReadAllText(current));
        Assert.Equal("old", File.ReadAllText(current + ".old"));
    }

    [Fact]
    public void SwapRestoresTheOldExeIfCopyingFails()
    {
        string current = Path.Combine(_dir, "EveMultibox.exe");
        File.WriteAllText(current, "old");
        Assert.ThrowsAny<IOException>(() => Updater.Swap(current, Path.Combine(_dir, "missing.exe")));
        Assert.Equal("old", File.ReadAllText(current));
        Assert.False(File.Exists(current + ".old"));
    }

    [Fact]
    public void TestBuildsAreNotReleaseBuilds() => Assert.False(Updater.IsReleaseBuild);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
