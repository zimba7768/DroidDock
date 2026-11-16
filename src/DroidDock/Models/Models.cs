using System.Globalization;

namespace DroidDock.Models;

/// <summary>An Android system image that is installed inside the SDK.</summary>
public sealed record SystemImage(string Dir, string Target, string ApiLevel, string TagIds, string TagDisplay, string Abi)
{
    public string TagId => TagIds.Split(',')[0];
    public bool HasPlayStore => TagIds.Contains("playstore", StringComparison.OrdinalIgnoreCase);
    public string DisplayName => $"{AndroidVersions.Describe(ApiLevel)} · {TagDisplay} · {Abi}";
    public override string ToString() => DisplayName;
}

/// <summary>An Android Virtual Device found in the AVD home folder.</summary>
public sealed record AvdInfo(string Name, string DisplayName, string Dir, string IniPath, string Target, string Abi, string TagDisplay)
{
    public string ApiLevel => Target.StartsWith("android-", StringComparison.OrdinalIgnoreCase) ? Target[8..] : Target;
}

public sealed record DeviceProfile(string Name, int Width, int Height, int Density, string Orientation)
{
    public static readonly IReadOnlyList<DeviceProfile> All =
    [
        new("Phone (1080 × 2400)", 1080, 2400, 420, "portrait"),
        new("Compact phone (720 × 1600)", 720, 1600, 320, "portrait"),
        new("Tablet (2560 × 1600)", 2560, 1600, 320, "landscape"),
        new("Desktop window (1920 × 1080)", 1920, 1080, 160, "landscape"),
    ];

    public override string ToString() => Name;
}

public sealed record AvdSpec(string Name, SystemImage Image, DeviceProfile Profile, int RamMb, int Cores, int DataGb);

public static class AndroidVersions
{
    private static readonly Dictionary<int, string> Names = new()
    {
        [24] = "7.0", [25] = "7.1", [26] = "8.0", [27] = "8.1", [28] = "9", [29] = "10", [30] = "11",
        [31] = "12", [32] = "12L", [33] = "13", [34] = "14", [35] = "15", [36] = "16", [37] = "17",
    };

    /// <summary>Numeric value of an API level string such as "36", "37.1" or "33-ext5".</summary>
    public static double Number(string apiLevel)
    {
        var digits = new string(apiLevel.TakeWhile(c => char.IsDigit(c) || c == '.').ToArray());
        return double.TryParse(digits, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : 0;
    }

    public static string Describe(string apiLevel)
    {
        var major = (int)Number(apiLevel);
        return Names.TryGetValue(major, out var name)
            ? $"Android {name} (API {apiLevel})"
            : $"Android API {apiLevel}";
    }
}
