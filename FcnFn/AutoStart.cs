using System.ComponentModel;
using System.Diagnostics;

namespace FcnFn;

internal static class AutoStart
{
    public const string TaskName = "FcnFn";

    // Argument STRING for elevated schtasks (ShellExecute/runas can't use ArgumentList).
    // /TR carries literal inner quotes so Task Scheduler stores a spaced exe path as a
    // single command rather than splitting it on the first space.
    public static string CreateArgString(string exePath) =>
        $"/Create /TN {TaskName} /TR \"\\\"{exePath}\\\"\" /SC ONLOGON /RL LIMITED /F";

    public static string DeleteArgString() => $"/Delete /TN {TaskName} /F";

    // Read-only query works without elevation.
    public static string[] QueryArgs() => new[] { "/Query", "/TN", TaskName };

    public static bool IsRegistered()
    {
        var psi = new ProcessStartInfo("schtasks.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in QueryArgs()) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode == 0;
    }

    public static void Register()
    {
        string exe = Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot determine executable path.");
        RunElevated(CreateArgString(exe));
    }

    public static void Unregister() => RunElevated(DeleteArgString());

    // Launch schtasks elevated (UAC). Returns false if the user declined the prompt;
    // rethrows any other failure.
    private static bool RunElevated(string arguments)
    {
        var psi = new ProcessStartInfo("schtasks.exe")
        {
            UseShellExecute = true,   // required for the runas verb
            Verb = "runas",           // triggers the UAC elevation prompt
            Arguments = arguments,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        try
        {
            using var p = Process.Start(psi);
            p?.WaitForExit();
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED
        {
            return false; // user declined elevation
        }
    }
}
