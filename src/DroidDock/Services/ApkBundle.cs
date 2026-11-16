using System.IO;
using System.IO.Compression;

namespace DroidDock.Services;

/// <summary>
/// App bundles (.xapk, .apks, .apkm) are zip files holding a base APK plus "split" APKs
/// that must all be installed together.
/// </summary>
public static class ApkBundle
{
    private static readonly string[] Extensions = [".xapk", ".apks", ".apkm"];

    public static bool IsApk(string path) => path.EndsWith(".apk", StringComparison.OrdinalIgnoreCase);

    public static bool HasBundleExtension(string path) =>
        Extensions.Any(e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// True for bundle file names, and for bundles renamed to .apk: a real APK has
    /// AndroidManifest.xml at its root, a bundle has APK files there instead.
    /// </summary>
    public static bool IsBundle(string path)
    {
        if (HasBundleExtension(path)) return true;
        if (!IsApk(path)) return false;
        try
        {
            using var zip = ZipFile.OpenRead(path);
            return zip.GetEntry("AndroidManifest.xml") == null && zip.Entries.Any(e => IsApk(e.Name));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Result of unpacking a bundle. Delete <see cref="Dir"/> when finished.</summary>
    public sealed record Extracted(string Dir, IReadOnlyList<string> Apks, bool HasObb);

    /// <summary>Unpacks every APK inside the bundle into a new temporary folder.</summary>
    public static Extracted Extract(string bundlePath)
    {
        var dir = Path.Combine(Path.GetTempPath(), "DroidDock-bundle-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var apks = new List<string>();
            var hasObb = false;
            using var zip = ZipFile.OpenRead(bundlePath);
            foreach (var entry in zip.Entries)
            {
                if (entry.Name.EndsWith(".obb", StringComparison.OrdinalIgnoreCase)) hasObb = true;
                if (!IsApk(entry.Name)) continue;

                // Flatten to the bare file name so a crafted entry path cannot escape the folder.
                var name = Path.GetFileName(entry.Name);
                var target = Path.Combine(dir, name);
                if (File.Exists(target)) target = Path.Combine(dir, $"{apks.Count}-{name}");
                entry.ExtractToFile(target);
                apks.Add(target);
            }
            return new Extracted(dir, apks, hasObb);
        }
        catch
        {
            TryDelete(dir);
            throw;
        }
    }

    public static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }
}
