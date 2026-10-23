using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EveMultibox.Shell;

/// <summary>A newer release that can be installed.</summary>
public sealed record UpdateInfo(Version Version, string Tag, string DownloadUrl, long Size, string? Sha256, string PageUrl, string Notes);

/// <summary>
/// Checks GitHub for a newer release, downloads its exe, verifies it, swaps it in place of the
/// running exe and restarts. Only release builds (made by the GitHub workflow) update themselves,
/// so development copies run from a build folder are never replaced.
/// </summary>
public sealed class Updater
{
    public const string Repository = "Brandin-Canterbury/eve-multibox-helper";
    public const string AssetName = "EveMultibox.exe";
    public static string LatestApiUrl => $"https://api.github.com/repos/{Repository}/releases/latest";
    public static string ReleasesPage => $"https://github.com/{Repository}/releases/latest";

    private readonly HttpClient _http;

    public Updater(HttpMessageHandler? handler = null)
    {
        _http = new HttpClient(handler ?? new HttpClientHandler()) { Timeout = TimeSpan.FromMinutes(10) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"EveMultiboxHelper/{CurrentVersion}");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    /// <summary>This build's version, e.g. 0.3.4 (from the release workflow's -p:Version).</summary>
    public static Version CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version is { } v ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : new Version(0, 0, 0);

    /// <summary>True only for builds made by the release workflow (it sets ReleaseChannel=github).</summary>
    public static bool IsReleaseBuild =>
        Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>()
            .Any(a => a.Key == "ReleaseChannel" && a.Value == "github");

    // ---- checking ----

    private sealed record ReleaseJson(
        [property: JsonPropertyName("tag_name")] string TagName,
        [property: JsonPropertyName("html_url")] string HtmlUrl,
        [property: JsonPropertyName("body")] string? Body,
        [property: JsonPropertyName("draft")] bool Draft,
        [property: JsonPropertyName("prerelease")] bool Prerelease,
        [property: JsonPropertyName("assets")] List<AssetJson> Assets);

    private sealed record AssetJson(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("size")] long Size,
        [property: JsonPropertyName("digest")] string? Digest,
        [property: JsonPropertyName("browser_download_url")] string Url);

    /// <summary>The latest release if it's newer than <paramref name="current"/>; otherwise null.</summary>
    public async Task<UpdateInfo?> CheckAsync(Version current, CancellationToken ct = default)
    {
        string json = await _http.GetStringAsync(LatestApiUrl, ct).ConfigureAwait(false);
        return Evaluate(json, current);
    }

    /// <summary>Reads GitHub's latest-release JSON and decides whether it's an installable update.</summary>
    internal static UpdateInfo? Evaluate(string releaseJson, Version current)
    {
        var release = JsonSerializer.Deserialize<ReleaseJson>(releaseJson);
        if (release is null || release.Draft || release.Prerelease) return null;
        if (ParseTag(release.TagName) is not { } version || version <= current) return null;

        var asset = release.Assets.FirstOrDefault(a => string.Equals(a.Name, AssetName, StringComparison.OrdinalIgnoreCase));
        if (asset is null || !IsTrustedDownload(asset.Url)) return null;

        string? sha = asset.Digest is { } d && d.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? d[7..].ToLowerInvariant() : null;
        return new UpdateInfo(version, release.TagName, asset.Url, asset.Size, sha, release.HtmlUrl, release.Body ?? "");
    }

    /// <summary>"v0.3.4" → 0.3.4. Anything else (e.g. "nightly") → null.</summary>
    internal static Version? ParseTag(string tag)
    {
        string s = tag.Trim().TrimStart('v', 'V');
        return Version.TryParse(s, out var v) ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : null;
    }

    /// <summary>Only ever download this repository's own release files over HTTPS.</summary>
    internal static bool IsTrustedDownload(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
        && uri.AbsolutePath.StartsWith($"/{Repository}/releases/download/", StringComparison.OrdinalIgnoreCase);

    // ---- installing ----

    public static string UpdatesDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EveMultibox", "updates");

    /// <summary>Downloads the new exe and checks its size and SHA-256 against what GitHub published.</summary>
    public async Task<string> DownloadAsync(UpdateInfo update, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(UpdatesDir);
        string target = Path.Combine(UpdatesDir, $"EveMultibox-{update.Version}.exe");
        string partial = target + ".part";

        using (var response = await _http.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? update.Size;
            await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var output = File.Create(partial);
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                done += read;
                if (total > 0) progress?.Report((double)done / total);
            }
        }

        Verify(partial, update);
        File.Move(partial, target, overwrite: true);
        return target;
    }

    /// <summary>Throws if the file isn't exactly what the release published.</summary>
    internal static void Verify(string file, UpdateInfo update)
    {
        long size = new FileInfo(file).Length;
        if (update.Size > 0 && size != update.Size)
        {
            File.Delete(file);
            throw new InvalidDataException($"The download is {size:N0} bytes but the release says {update.Size:N0}. Try again later.");
        }
        if (update.Sha256 is { } expected)
        {
            using var stream = File.OpenRead(file);
            string actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            stream.Close();
            if (actual != expected)
            {
                File.Delete(file);
                throw new InvalidDataException("The download's checksum doesn't match the release, so it wasn't installed.");
            }
        }
    }

    /// <summary>
    /// Puts the new exe where the running one is. Windows won't overwrite a running exe but does let
    /// it be renamed, so the old one becomes "*.old" (removed on the next start). If the move fails,
    /// the old exe is put back.
    /// </summary>
    internal static void Swap(string currentExe, string newExe)
    {
        string old = currentExe + ".old";
        if (File.Exists(old)) File.Delete(old);
        File.Move(currentExe, old);
        try
        {
            File.Copy(newExe, currentExe);
        }
        catch
        {
            if (File.Exists(currentExe)) File.Delete(currentExe);
            File.Move(old, currentExe);
            throw;
        }
    }

    /// <summary>Swaps in the downloaded exe and starts it. The caller then quits this instance.</summary>
    public static void InstallAndRelaunch(string downloadedExe)
    {
        string current = Environment.ProcessPath ?? throw new InvalidOperationException("Can't tell where the helper's exe is.");
        Swap(current, downloadedExe);
        // The new copy waits for this one to exit first, so they don't fight over hotkeys and windows.
        Process.Start(new ProcessStartInfo(current, $"--updated --wait-for {Environment.ProcessId}")
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(current)!,
        });
    }

    /// <summary>After an update: waits (up to 15 s) for the previous copy, given by --wait-for, to exit.</summary>
    public static void WaitForPreviousInstance(string[] args)
    {
        int i = Array.IndexOf(args, "--wait-for");
        if (i < 0 || i + 1 >= args.Length || !int.TryParse(args[i + 1], out int pid)) return;
        try
        {
            using var previous = Process.GetProcessById(pid);
            previous.WaitForExit(15_000);
        }
        catch (ArgumentException)
        {
            // Already gone.
        }
    }

    /// <summary>Removes the previous version's exe and old downloads (call at start-up).</summary>
    public static void CleanUpOldFiles()
    {
        try
        {
            if (Environment.ProcessPath is { } exe && File.Exists(exe + ".old")) File.Delete(exe + ".old");
            if (Directory.Exists(UpdatesDir))
                foreach (var f in Directory.EnumerateFiles(UpdatesDir)) File.Delete(f);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The old exe may still be closing; it'll be cleaned up next time.
        }
    }
}
