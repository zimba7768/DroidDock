using System.IO;
using DroidDock.Models;

namespace DroidDock.Services;

/// <summary>Locations of the Android SDK pieces DroidDock drives.</summary>
public sealed class SdkPaths(string root)
{
    public string Root { get; } = root;
    public string EmulatorExe => Path.Combine(Root, "emulator", "emulator.exe");
    public string AdbExe => Path.Combine(Root, "platform-tools", "adb.exe");
    public string SystemImagesDir => Path.Combine(Root, "system-images");

    public bool HasEmulator => File.Exists(EmulatorExe);
    public bool HasAdb => File.Exists(AdbExe);
    public bool IsReady => HasEmulator && HasAdb;

    public IReadOnlyDictionary<string, string> Env => new Dictionary<string, string>
    {
        ["ANDROID_SDK_ROOT"] = Root,
        ["ANDROID_HOME"] = Root,
    };

    /// <summary>The SDK DroidDock manages itself when no other SDK is on the machine.</summary>
    public static string ManagedRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DroidDock", "sdk");

    /// <summary>
    /// Finds an SDK: the environment variables first, then Android Studio's default folder,
    /// then DroidDock's own. Prefers a folder that already contains the emulator.
    /// </summary>
    public static SdkPaths Locate()
    {
        var candidates = new[]
        {
            Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT"),
            Environment.GetEnvironmentVariable("ANDROID_HOME"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Android", "Sdk"),
            ManagedRoot,
        }
        .Where(p => !string.IsNullOrWhiteSpace(p))
        .Select(p => new SdkPaths(Path.GetFullPath(p!)))
        .ToList();

        return candidates.FirstOrDefault(c => c.HasEmulator)
            ?? candidates.FirstOrDefault(c => Directory.Exists(c.Root))
            ?? new SdkPaths(ManagedRoot);
    }

    /// <summary>System images present under system-images/&lt;target&gt;/&lt;tag&gt;/&lt;abi&gt;.</summary>
    public IReadOnlyList<SystemImage> InstalledImages()
    {
        var result = new List<SystemImage>();
        if (!Directory.Exists(SystemImagesDir)) return result;

        foreach (var targetDir in Directory.GetDirectories(SystemImagesDir))
        foreach (var tagDir in Directory.GetDirectories(targetDir))
        foreach (var abiDir in Directory.GetDirectories(tagDir))
        {
            if (!File.Exists(Path.Combine(abiDir, "system.img"))) continue;

            var target = Path.GetFileName(targetDir);
            var props = IniFile.Read(Path.Combine(abiDir, "source.properties"));
            var api = props.GetValueOrDefault("AndroidVersion.ApiLevel")
                      ?? (target.StartsWith("android-") ? target[8..] : target);
            var tagIds = props.GetValueOrDefault("SystemImage.TagId") ?? Path.GetFileName(tagDir);
            var tagDisplay = props.GetValueOrDefault("SystemImage.TagDisplay") ?? tagIds;
            var abi = props.GetValueOrDefault("SystemImage.Abi") ?? Path.GetFileName(abiDir);
            result.Add(new SystemImage(abiDir, target, api, tagIds, tagDisplay.Split(',')[0], abi));
        }

        return result.OrderByDescending(i => AndroidVersions.Number(i.ApiLevel)).ToList();
    }
}
