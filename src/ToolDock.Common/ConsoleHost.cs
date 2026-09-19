using System.Runtime.InteropServices;
using System.Text;

namespace ToolDock.Common;

public static class ConsoleHost
{
    private const uint AttachParentProcess = 0xFFFFFFFF;

    public static bool TryAttachParent()
    {
        if (Environment.GetEnvironmentVariable("TOOLDOCK_NO_CONSOLE") == "1" ||
            !OperatingSystem.IsWindows() ||
            !AttachConsole(AttachParentProcess))
        {
            return false;
        }

        try
        {
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true });
            Console.SetError(new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true });
            return true;
        }
        catch
        {
            return false;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(uint processId);
}
