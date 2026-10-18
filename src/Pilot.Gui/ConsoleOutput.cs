using System.Runtime.InteropServices;

namespace Pilot.Gui;

/// <summary>
/// Gives the WinExe a stdout. A Windows GUI program has no console, so what it prints goes nowhere unless its
/// output is redirected; started from a terminal, it borrows that terminal's console, so whoever started it sees what
/// it shows and where its data comes from (the controller's window rule).
/// </summary>
internal static partial class ConsoleOutput
{
    private const int StdOutputHandle = -11;
    private const uint AttachParentProcess = unchecked((uint)-1);

    public static void AttachToParent()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // Redirected (a pipe, a file): leave it, it already goes where the starter wants it.
        if (GetStdHandle(StdOutputHandle) is not 0 and not -1)
        {
            return;
        }

        if (AttachConsole(AttachParentProcess))
        {
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
            Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachConsole(uint processId);

    [LibraryImport("kernel32.dll")]
    private static partial nint GetStdHandle(int stdHandle);
}
