using System.Diagnostics;

namespace DroidDock.Services;

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Ok => ExitCode == 0;
    public string Combined => string.Join(Environment.NewLine,
        new[] { StdOut.Trim(), StdErr.Trim() }.Where(s => s.Length > 0));
}

public static class ProcessRunner
{
    /// <summary>Runs a console program hidden, captures its output and never throws on non-zero exit.</summary>
    public static async Task<ProcessResult> RunAsync(
        string exe,
        IEnumerable<string> args,
        IReadOnlyDictionary<string, string>? env = null,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (env != null)
            foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(60));

        using var p = new Process { StartInfo = psi };
        try
        {
            p.Start();
        }
        catch (Exception ex)
        {
            return new ProcessResult(-1, "", $"Could not start {exe}: {ex.Message}");
        }

        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        try
        {
            await p.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
            ct.ThrowIfCancellationRequested();
            return new ProcessResult(-1, "", $"{System.IO.Path.GetFileName(exe)} timed out.");
        }

        return new ProcessResult(p.ExitCode, await stdout, await stderr);
    }
}
