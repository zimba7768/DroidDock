namespace DroidDock.Services;

public sealed record AdbDevice(string Serial, string State)
{
    public bool IsEmulator => Serial.StartsWith("emulator-", StringComparison.Ordinal);
    public bool IsOnline => State == "device";
}

/// <summary>Thin wrapper around adb.exe.</summary>
public sealed class AdbService(SdkPaths sdk)
{
    private Task<ProcessResult> Adb(TimeSpan timeout, params string[] args) =>
        ProcessRunner.RunAsync(sdk.AdbExe, args, sdk.Env, timeout);

    private Task<ProcessResult> Adb(params string[] args) => Adb(TimeSpan.FromSeconds(30), args);

    public static IReadOnlyList<AdbDevice> ParseDevices(string output) =>
        output.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith("List of devices") && !l.StartsWith('*'))
            .Select(l => l.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length >= 2)
            .Select(parts => new AdbDevice(parts[0], parts[1]))
            .ToList();

    public static IReadOnlyList<string> ParsePackages(string output) =>
        output.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("package:", StringComparison.Ordinal))
            .Select(l => l["package:".Length..])
            .Where(p => p.Length > 0)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public async Task<IReadOnlyList<AdbDevice>> DevicesAsync()
    {
        var r = await Adb("devices");
        return r.Ok ? ParseDevices(r.StdOut) : [];
    }

    /// <summary>Asks a running emulator which virtual device it is.</summary>
    public async Task<string?> EmulatorAvdNameAsync(string serial)
    {
        var r = await Adb(TimeSpan.FromSeconds(10), "-s", serial, "emu", "avd", "name");
        if (!r.Ok) return null;
        return r.StdOut.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && l != "OK");
    }

    public async Task<bool> IsBootedAsync(string serial)
    {
        var r = await Adb(TimeSpan.FromSeconds(10), "-s", serial, "shell", "getprop", "sys.boot_completed");
        return r.Ok && r.StdOut.Trim() == "1";
    }

    public async Task<IReadOnlyList<string>> ListPackagesAsync(string serial, bool includeSystem)
    {
        var r = includeSystem
            ? await Adb("-s", serial, "shell", "pm", "list", "packages")
            : await Adb("-s", serial, "shell", "pm", "list", "packages", "-3");
        return r.Ok ? ParsePackages(r.StdOut) : [];
    }

    public Task<ProcessResult> InstallAsync(string serial, string apkPath) =>
        Adb(TimeSpan.FromMinutes(10), "-s", serial, "install", "-r", apkPath);

    public Task<ProcessResult> UninstallAsync(string serial, string package) =>
        Adb(TimeSpan.FromMinutes(2), "-s", serial, "uninstall", package);

    /// <summary>Starts the app's launcher activity.</summary>
    public Task<ProcessResult> LaunchAsync(string serial, string package) =>
        Adb("-s", serial, "shell", "monkey", "-p", package, "-c", "android.intent.category.LAUNCHER", "1");

    public Task<ProcessResult> KillEmulatorAsync(string serial) =>
        Adb(TimeSpan.FromSeconds(15), "-s", serial, "emu", "kill");
}
