namespace FcnFn;

internal static class DiagConsole
{
    private static bool _enabled;

    public static bool Enabled => _enabled;

    public static void Toggle()
    {
        if (_enabled)
        {
            Native.FreeConsole();
            _enabled = false;
        }
        else if (Native.AllocConsole())
        {
            // Re-point Console's cached streams at the new console buffer.
            var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
            Console.SetOut(stdout);
            Console.SetError(stdout);
            _enabled = true;
            Console.WriteLine("DIAG on - vk / scan / flags / ext / inj / msg. Toggle off from the tray.");
        }
    }

    public static void Log(in Native.KBDLLHOOKSTRUCT kb, bool isDown, bool isMarker)
    {
        if (!_enabled) return;
        bool inj = (kb.flags & Native.LLKHF_INJECTED) != 0;
        Console.WriteLine($" 0x{kb.vkCode:X2}    0x{kb.scanCode:X3}   0x{kb.flags:X2}    " +
                          $"{((kb.flags & 1) != 0 ? "E" : " ")}   {(inj ? "I" : " ")}   " +
                          $"{(isDown ? "DOWN" : "UP  ")}{(isMarker ? "  <ours>" : "")}");
    }

    public static void Error(string message)
    {
        if (_enabled) Console.Error.WriteLine(message);
    }
}
