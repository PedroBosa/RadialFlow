using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace RadialFlow.Tests.TestSupport;

/// <summary>O parser de linha de comando do próprio Windows (o mesmo que o processo filho usa para montar argv).</summary>
internal static class WindowsCommandLine
{
    /// <summary>argv[0] é o executável; o restante são os argumentos.</summary>
    public static string[] Parse(string commandLine)
    {
        IntPtr argv = CommandLineToArgvW(commandLine, out int argc);
        if (argv == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());

        try
        {
            var result = new string[argc];
            for (int i = 0; i < argc; i++)
                result[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size)) ?? "";
            return result;
        }
        finally
        {
            LocalFree(argv);
        }
    }

    [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int argumentCount);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
