using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Xml.Linq;
using DroidDock.Models;

namespace DroidDock.Services;

/// <summary>A downloadable SDK package from Google's repository index.</summary>
public sealed record RemotePackage(
    string Path,
    string DisplayName,
    Version Revision,
    string LicenseRef,
    Uri Url,
    long Size,
    string Checksum,
    string ChecksumType,
    string? ApiLevel = null,
    string? TagId = null,
    string? TagDisplay = null,
    string? Abi = null)
{
    public bool IsSystemImage => Path.StartsWith("system-images;", StringComparison.Ordinal);
    public string SizeText => Size >= 1 << 30 ? $"{Size / (double)(1 << 30):0.0} GB" : $"{Size / (double)(1 << 20):0} MB";
    public string ImageDisplayName => $"{AndroidVersions.Describe(ApiLevel ?? "?")} · {TagDisplay} · {SizeText}";
}

public sealed record InstallProgress(string Stage, long Done, long Total);

/// <summary>
/// Reads Google's public SDK repository XML and installs packages from it without
/// needing Java or sdkmanager.
/// </summary>
public sealed class SdkRepository
{
    public static readonly Uri MainFeed = new("https://dl.google.com/android/repository/repository2-3.xml");
    public static readonly Uri PlayStoreImagesFeed = new("https://dl.google.com/android/repository/sys-img/google_apis_playstore/sys-img2-3.xml");
    public static readonly Uri GoogleApisImagesFeed = new("https://dl.google.com/android/repository/sys-img/google_apis/sys-img2-3.xml");

    public List<RemotePackage> Packages { get; } = [];
    public Dictionary<string, string> Licenses { get; } = [];

    private static string HostArch => RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "aarch64" : "x64";
    private static string HostAbi => RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64-v8a" : "x86_64";

    public static async Task<SdkRepository> FetchAsync(HttpClient http, CancellationToken ct = default)
    {
        var repo = new SdkRepository();
        foreach (var feed in new[] { MainFeed, PlayStoreImagesFeed, GoogleApisImagesFeed })
            repo.Add(await http.GetStringAsync(feed, ct), feed);
        return repo;
    }

    private static XElement? El(XElement parent, string name) =>
        parent.Elements().FirstOrDefault(e => e.Name.LocalName == name);

    private static int Int(XElement? parent, string name) =>
        parent != null && int.TryParse(El(parent, name)?.Value, out var n) ? n : 0;

    /// <summary>Adds the stable, Windows-compatible packages of one repository XML feed.</summary>
    public void Add(string xml, Uri feedUri)
    {
        var root = XDocument.Parse(xml).Root ?? throw new InvalidDataException("Empty repository index.");

        foreach (var lic in root.Elements().Where(e => e.Name.LocalName == "license"))
            Licenses[(string?)lic.Attribute("id") ?? ""] = lic.Value.Trim();

        var stableChannels = root.Elements()
            .Where(e => e.Name.LocalName == "channel" && e.Value.Trim() == "stable")
            .Select(e => (string?)e.Attribute("id"))
            .ToHashSet();

        foreach (var rp in root.Elements().Where(e => e.Name.LocalName == "remotePackage"))
        {
            var path = (string?)rp.Attribute("path");
            if (path == null) continue;
            if (!stableChannels.Contains((string?)El(rp, "channelRef")?.Attribute("ref"))) continue;

            var archive = El(rp, "archives")?.Elements()
                .Where(a => a.Name.LocalName == "archive")
                .FirstOrDefault(a =>
                    (El(a, "host-os")?.Value ?? "windows") == "windows" &&
                    (El(a, "host-arch")?.Value ?? HostArch) == HostArch);
            var complete = archive == null ? null : El(archive, "complete");
            var url = complete == null ? null : El(complete, "url")?.Value;
            var checksum = complete == null ? null : El(complete, "checksum");
            if (complete == null || url == null || checksum == null) continue;

            var rev = El(rp, "revision");
            var details = El(rp, "type-details");
            var tag = details == null ? null : El(details, "tag");

            Packages.Add(new RemotePackage(
                path,
                El(rp, "display-name")?.Value ?? path,
                new Version(Int(rev, "major"), Int(rev, "minor"), Int(rev, "micro")),
                (string?)El(rp, "uses-license")?.Attribute("ref") ?? "",
                new Uri(feedUri, url),
                long.TryParse(El(complete, "size")?.Value, out var size) ? size : 0,
                checksum.Value.Trim().ToLowerInvariant(),
                ((string?)checksum.Attribute("type") ?? "sha1").ToLowerInvariant(),
                details == null ? null : El(details, "api-level")?.Value,
                tag == null ? null : El(tag, "id")?.Value,
                tag == null ? null : El(tag, "display")?.Value,
                details == null ? null : El(details, "abi")?.Value));
        }
    }

    /// <summary>Newest stable revision of a package such as "emulator" or "platform-tools".</summary>
    public RemotePackage? Latest(string path) =>
        Packages.Where(p => p.Path == path).OrderByDescending(p => p.Revision).FirstOrDefault();

    /// <summary>System images that run on this PC, newest Android first.</summary>
    public IReadOnlyList<RemotePackage> SystemImages(int minApi = 30) =>
        Packages.Where(p => p.IsSystemImage && p.Abi == HostAbi && p.ApiLevel != null
                            && AndroidVersions.Number(p.ApiLevel) >= minApi
                            && !p.Path.Contains("-ext", StringComparison.Ordinal))
            .GroupBy(p => p.Path)
            .Select(g => g.OrderByDescending(p => p.Revision).First())
            .OrderByDescending(p => AndroidVersions.Number(p.ApiLevel!))
            .ThenByDescending(p => p.TagId == "google_apis_playstore")
            .ToList();

    /// <summary>"system-images;android-36;google_apis;x86_64" → &lt;sdk&gt;\system-images\android-36\google_apis\x86_64</summary>
    public static string InstallDir(string sdkRoot, string packagePath) =>
        System.IO.Path.Combine([sdkRoot, .. packagePath.Split(';')]);

    public static async Task InstallAsync(
        HttpClient http, RemotePackage pkg, string sdkRoot, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        var temp = System.IO.Path.Combine(sdkRoot, ".droiddock-temp");
        Directory.CreateDirectory(temp);
        var zipPath = System.IO.Path.Combine(temp, System.IO.Path.GetFileName(pkg.Url.LocalPath));
        var staging = System.IO.Path.Combine(temp, "extract-" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            await DownloadAsync(http, pkg, zipPath, progress, ct);

            progress?.Report(new InstallProgress($"Verifying {pkg.DisplayName}…", 0, 0));
            var actual = await HashAsync(zipPath, pkg.ChecksumType, ct);
            if (!string.Equals(actual, pkg.Checksum, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Checksum mismatch for {pkg.DisplayName}. The download was discarded.");

            progress?.Report(new InstallProgress($"Unpacking {pkg.DisplayName}…", 0, 0));
            await Task.Run(() => ExtractStrippingTopFolder(zipPath, staging), ct);

            var target = InstallDir(sdkRoot, pkg.Path);
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
            Directory.Move(staging, target);
        }
        finally
        {
            try { if (File.Exists(zipPath)) File.Delete(zipPath); } catch { /* best effort */ }
            try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { /* best effort */ }
        }
    }

    private static async Task DownloadAsync(
        HttpClient http, RemotePackage pkg, string zipPath, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        using var response = await http.GetAsync(pkg.Url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? pkg.Size;

        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var file = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
        var buffer = new byte[1 << 16];
        long done = 0, lastReport = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, read), ct);
            done += read;
            if (done - lastReport >= 1 << 20 || done == total)
            {
                lastReport = done;
                progress?.Report(new InstallProgress($"Downloading {pkg.DisplayName}…", done, total));
            }
        }
    }

    private static async Task<string> HashAsync(string path, string type, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        var hash = type is "sha-256" or "sha256"
            ? await SHA256.HashDataAsync(stream, ct)
            : await SHA1.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Extracts a zip, dropping its single top-level folder (SDK archives all have one),
    /// and refuses entries that would escape the destination.
    /// </summary>
    internal static void ExtractStrippingTopFolder(string zipPath, string destination)
    {
        Directory.CreateDirectory(destination);
        var destFull = System.IO.Path.GetFullPath(destination).TrimEnd('\\') + "\\";

        using var zip = ZipFile.OpenRead(zipPath);
        var names = zip.Entries.Select(e => e.FullName.Replace('\\', '/')).ToList();
        var tops = names.Select(n => n.Split('/')[0]).Distinct().ToList();
        var strip = tops.Count == 1 && names.All(n => n.Contains('/')) ? tops[0].Length + 1 : 0;

        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            if (name.Length <= strip) continue;
            var rel = name[strip..];
            var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(destFull, rel));
            if (!full.StartsWith(destFull, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Archive entry '{entry.FullName}' is outside the install folder.");

            if (rel.EndsWith('/'))
            {
                Directory.CreateDirectory(full);
                continue;
            }
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
            entry.ExtractToFile(full, overwrite: true);
        }
    }
}
