using System.IO;
using System.Text.RegularExpressions;
using DroidDock.Models;

namespace DroidDock.Services;

/// <summary>
/// Lists, creates and deletes Android Virtual Devices by reading and writing their
/// config files directly, so neither Java nor avdmanager is required.
/// </summary>
public static partial class AvdService
{
    [GeneratedRegex(@"^[A-Za-z0-9._-]+$")]
    private static partial Regex ValidName();

    public static string AvdHome
    {
        get
        {
            var explicitHome = Environment.GetEnvironmentVariable("ANDROID_AVD_HOME");
            if (!string.IsNullOrWhiteSpace(explicitHome)) return explicitHome;
            var userHome = Environment.GetEnvironmentVariable("ANDROID_USER_HOME");
            if (!string.IsNullOrWhiteSpace(userHome)) return Path.Combine(userHome, "avd");
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".android", "avd");
        }
    }

    public static bool IsValidName(string name) => ValidName().IsMatch(name);

    public static IReadOnlyList<AvdInfo> List() => List(AvdHome);

    public static IReadOnlyList<AvdInfo> List(string avdHome)
    {
        var result = new List<AvdInfo>();
        if (!Directory.Exists(avdHome)) return result;

        foreach (var ini in Directory.GetFiles(avdHome, "*.ini"))
        {
            var name = Path.GetFileNameWithoutExtension(ini);
            var pointer = IniFile.Read(ini);
            var dir = pointer.GetValueOrDefault("path");
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                dir = Path.Combine(avdHome, name + ".avd");
            if (!Directory.Exists(dir)) continue;

            var config = IniFile.Read(Path.Combine(dir, "config.ini"));
            result.Add(new AvdInfo(
                name,
                config.GetValueOrDefault("avd.ini.displayname") ?? name.Replace('_', ' '),
                dir,
                ini,
                config.GetValueOrDefault("target") ?? pointer.GetValueOrDefault("target") ?? "",
                config.GetValueOrDefault("abi.type") ?? "",
                config.GetValueOrDefault("tag.display") ?? ""));
        }

        return result.OrderBy(a => a.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static Dictionary<string, string> BuildConfig(AvdSpec spec, string sdkRoot)
    {
        var image = spec.Image;
        var profile = spec.Profile;
        var sysdir = Path.GetRelativePath(sdkRoot, image.Dir).TrimEnd('\\', '/') + "\\";

        return new Dictionary<string, string>
        {
            ["AvdId"] = spec.Name,
            ["PlayStore.enabled"] = image.HasPlayStore ? "true" : "false",
            ["abi.type"] = image.Abi,
            ["avd.ini.displayname"] = spec.Name.Replace('_', ' '),
            ["avd.ini.encoding"] = "UTF-8",
            ["disk.dataPartition.size"] = $"{spec.DataGb}G",
            ["fastboot.forceColdBoot"] = "no",
            ["fastboot.forceFastBoot"] = "yes",
            ["hw.accelerometer"] = "yes",
            ["hw.audioInput"] = "yes",
            ["hw.battery"] = "yes",
            ["hw.camera.back"] = "virtualscene",
            ["hw.camera.front"] = "emulated",
            ["hw.cpu.arch"] = image.Abi == "arm64-v8a" ? "arm64" : image.Abi,
            ["hw.cpu.ncore"] = spec.Cores.ToString(),
            ["hw.dPad"] = "no",
            ["hw.gps"] = "yes",
            ["hw.gpu.enabled"] = "yes",
            ["hw.gpu.mode"] = "auto",
            ["hw.initialOrientation"] = profile.Orientation,
            ["hw.keyboard"] = "yes",
            ["hw.lcd.density"] = profile.Density.ToString(),
            ["hw.lcd.height"] = profile.Height.ToString(),
            ["hw.lcd.width"] = profile.Width.ToString(),
            ["hw.mainKeys"] = "no",
            ["hw.ramSize"] = spec.RamMb.ToString(),
            ["hw.sdCard"] = "yes",
            ["hw.sensors.orientation"] = "yes",
            ["hw.sensors.proximity"] = "yes",
            ["hw.trackBall"] = "no",
            ["image.sysdir.1"] = sysdir,
            ["runtime.network.latency"] = "none",
            ["runtime.network.speed"] = "full",
            ["sdcard.size"] = "512M",
            ["showDeviceFrame"] = "no",
            ["skin.dynamic"] = "yes",
            ["skin.name"] = $"{profile.Width}x{profile.Height}",
            ["skin.path"] = "_no_skin",
            ["tag.display"] = image.TagDisplay,
            ["tag.id"] = image.TagId,
            ["tag.ids"] = image.TagIds,
            ["target"] = image.Target,
            ["vm.heapSize"] = "384",
        };
    }

    public static AvdInfo Create(AvdSpec spec, string sdkRoot) => Create(spec, sdkRoot, AvdHome);

    public static AvdInfo Create(AvdSpec spec, string sdkRoot, string avdHome)
    {
        if (!IsValidName(spec.Name))
            throw new ArgumentException("Device names may only contain letters, digits, '.', '_' and '-'.");

        var dir = Path.Combine(avdHome, spec.Name + ".avd");
        var ini = Path.Combine(avdHome, spec.Name + ".ini");
        if (Directory.Exists(dir) || File.Exists(ini))
            throw new InvalidOperationException($"A device named '{spec.Name}' already exists.");

        Directory.CreateDirectory(dir);
        IniFile.Write(Path.Combine(dir, "config.ini"), BuildConfig(spec, sdkRoot));
        IniFile.Write(ini, new Dictionary<string, string>
        {
            ["avd.ini.encoding"] = "UTF-8",
            ["path"] = dir,
            ["path.rel"] = $"avd\\{spec.Name}.avd",
            ["target"] = spec.Image.Target,
        });

        return List(avdHome).First(a => a.Name == spec.Name);
    }

    public static void Delete(AvdInfo avd)
    {
        if (Directory.Exists(avd.Dir)) Directory.Delete(avd.Dir, recursive: true);
        if (File.Exists(avd.IniPath)) File.Delete(avd.IniPath);
    }
}
