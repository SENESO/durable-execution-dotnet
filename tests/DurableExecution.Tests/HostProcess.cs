using System.Diagnostics;

namespace DurableExecution.Tests;

/// <summary>The result of one scenario-host process run.</summary>
public sealed record HostRun(int ExitCode, string Output)
{
    /// <summary>
    /// True when the run was hard-killed at a crash point, not exited. The
    /// exit code of a self-<see cref="Process.Kill()"/> is platform-shaped:
    /// -9 (or a shell's 137) on Unix, -1 on Windows — the [CRASH] marker in
    /// the output is what proves the kill landed at the crash point.
    /// </summary>
    public bool WasKilled =>
        (ExitCode == 137 || ExitCode == -9 || (OperatingSystem.IsWindows() && ExitCode == -1))
        && Output.Contains("[CRASH]");
}

/// <summary>
/// Drives the scenario host as a real child process — the suite's crashes are
/// process kills, not exceptions, so recovery is proven across process
/// boundaries exactly as the scenarios specify.
/// </summary>
public static class HostProcess
{
    private static string HostPath()
    {
        // Walk up from the test assembly's own directory (not AppContext's —
        // under a console runner that is the runner's directory) to the
        // solution root, then into the TestHost output of the same build.
        string assemblyDir = Path.GetDirectoryName(typeof(HostProcess).Assembly.Location)!;
        var dir = new DirectoryInfo(assemblyDir);
        string config = assemblyDir.Contains("Release") ? "Release" : "Debug";
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DurableExecution.sln")))
            dir = dir.Parent;
        if (dir is null)
            throw new InvalidOperationException("solution root not found above " + assemblyDir);
        string binDir = Path.Combine(dir.FullName, "tests", "DurableExecution.TestHost", "bin", config, "net8.0");
        string apphost = Path.Combine(binDir, "DurableExecution.TestHost");
        if (File.Exists(apphost))
            return apphost;
        string dll = Path.Combine(binDir, "DurableExecution.TestHost.dll");
        if (File.Exists(dll))
            return dll;
        throw new InvalidOperationException("TestHost build output not found in " + binDir);
    }

    public static HostRun Run(
        string stateDir,
        string crashMode = "",
        string gatewayMode = "idempotent",
        string variant = "default",
        IReadOnlyDictionary<string, string>? extraEnv = null,
        params string[] args)
    {
        var psi = NewStartInfo(stateDir, crashMode, gatewayMode, variant, extraEnv, args);
        using var process = Process.Start(psi)!;
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        return new HostRun(process.ExitCode, output);
    }

    public static Process Start(
        string stateDir,
        string crashMode = "",
        string gatewayMode = "idempotent",
        string variant = "default",
        IReadOnlyDictionary<string, string>? extraEnv = null,
        params string[] args)
    {
        return Process.Start(NewStartInfo(stateDir, crashMode, gatewayMode, variant, extraEnv, args))!;
    }

    private static ProcessStartInfo NewStartInfo(
        string stateDir,
        string crashMode,
        string gatewayMode,
        string variant,
        IReadOnlyDictionary<string, string>? extraEnv,
        string[] args)
    {
        string host = HostPath();
        var psi = new ProcessStartInfo
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        if (host.EndsWith(".dll"))
        {
            psi.FileName = "dotnet";
            psi.ArgumentList.Add("exec");
            psi.ArgumentList.Add(host);
        }
        else
        {
            psi.FileName = host;
        }
        foreach (string arg in args)
            psi.ArgumentList.Add(arg);
        psi.Environment["PROOF_STATE"] = stateDir;
        psi.Environment["CRASH_MODE"] = crashMode;
        psi.Environment["GATEWAY_MODE"] = gatewayMode;
        psi.Environment["WORKFLOW_VARIANT"] = variant;
        if (extraEnv is not null)
            foreach (var kv in extraEnv)
                psi.Environment[kv.Key] = kv.Value;
        return psi;
    }

    /// <summary>A fresh, empty state directory for one scenario run.</summary>
    public static string FreshState(string scenario)
    {
        string dir = Path.Combine(Path.GetTempPath(), "p0-tests",
            scenario + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }
}
