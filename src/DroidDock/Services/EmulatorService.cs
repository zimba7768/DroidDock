using System.Diagnostics;

namespace DroidDock.Services;

public static class EmulatorService
{
    /// <summary>
    /// Launches the emulator window for a virtual device. Output lines are forwarded to
    /// <paramref name="onOutput"/> and <paramref name="onExit"/> receives the exit code.
    /// </summary>
    public static Process Start(SdkPaths sdk, string avdName, Action<string> onOutput, Action<int> onExit)
    {
        var psi = new ProcessStartInfo(sdk.EmulatorExe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = System.IO.Path.GetDirectoryName(sdk.EmulatorExe)!,
        };
        psi.ArgumentList.Add("-avd");
        psi.ArgumentList.Add(avdName);
        foreach (var kv in sdk.Env) psi.Environment[kv.Key] = kv.Value;

        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.OutputDataReceived += (_, e) => { if (e.Data != null) onOutput(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) onOutput(e.Data); };
        p.Exited += (_, _) =>
        {
            int code;
            try { code = p.ExitCode; } catch { code = -1; }
            onExit(code);
        };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        return p;
    }

    /// <summary>Reports whether hardware acceleration (WHPX / AEHD) is usable.</summary>
    public static async Task<string> AccelCheckAsync(SdkPaths sdk)
    {
        var r = await ProcessRunner.RunAsync(sdk.EmulatorExe, ["-accel-check"], sdk.Env, TimeSpan.FromSeconds(20));
        var lines = r.Combined.Split('\n').Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith("accel", StringComparison.OrdinalIgnoreCase) && !l.All(char.IsDigit));
        return string.Join(" ", lines);
    }
}
