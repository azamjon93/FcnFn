using System.Diagnostics;

namespace FcnFn;

internal static class AutoStart
{
    public const string TaskName = "FcnFn";

    public static string[] CreateArgs(string exePath) => new[]
    {
        "/Create", "/TN", TaskName, "/TR", $"\"{exePath}\"",
        "/SC", "ONLOGON", "/RL", "LIMITED", "/F"
    };

    public static string[] DeleteArgs() => new[] { "/Delete", "/TN", TaskName, "/F" };

    public static string[] QueryArgs() => new[] { "/Query", "/TN", TaskName };

    private static int Run(string[] args)
    {
        var psi = new ProcessStartInfo("schtasks.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)!;
        p.WaitForExit();
        return p.ExitCode;
    }

    public static bool IsRegistered() => Run(QueryArgs()) == 0;

    public static void Register()
    {
        string exe = Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot determine executable path.");
        Run(CreateArgs(exe));
    }

    public static void Unregister() => Run(DeleteArgs());
}
