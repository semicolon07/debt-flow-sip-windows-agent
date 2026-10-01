using System.Runtime.InteropServices;
using System.Text;

namespace DebtFlow.SipAgent.Host;

public static class ConsoleSession
{
    private const uint AttachParentProcess = 0xFFFFFFFF;

    public static void EnsureAttached()
    {
        if (!AttachConsole(AttachParentProcess))
        {
            _ = AllocConsole();
        }

        Console.OutputEncoding = Encoding.UTF8;
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
        Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllocConsole();
}
