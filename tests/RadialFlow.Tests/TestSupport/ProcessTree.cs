using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RadialFlow.Tests.TestSupport;

/// <summary>Consulta a árvore de processos: quem é filho de quem e se um PID ainda existe.</summary>
internal static class ProcessTree
{
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    /// <summary>true se o PID não existe mais (ou o processo já terminou).</summary>
    public static bool IsGone(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            return process.HasExited;
        }
        catch (ArgumentException)
        {
            return true;   // nenhum processo com esse PID
        }
    }

    /// <summary>PIDs dos processos com o nome de imagem informado (sem ".exe") cujo pai é <paramref name="parentPid"/>.</summary>
    public static IReadOnlyList<int> FindChildren(int parentPid, string imageName)
    {
        var result = new List<int>();
        foreach (Process candidate in Process.GetProcessesByName(imageName))
        {
            using (candidate)
            {
                if (GetParentProcessId(candidate.Id) == parentPid) result.Add(candidate.Id);
            }
        }
        return result;
    }

    private static int GetParentProcessId(int pid)
    {
        IntPtr handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (handle == IntPtr.Zero) return -1;

        try
        {
            var info = new ProcessBasicInformation();
            return NtQueryInformationProcess(handle, 0, ref info, Marshal.SizeOf<ProcessBasicInformation>(), out _) == 0
                ? (int)info.InheritedFromUniqueProcessId
                : -1;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public IntPtr ExitStatus, PebBaseAddress, AffinityMask, BasePriority, UniqueProcessId, InheritedFromUniqueProcessId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr process, int infoClass, ref ProcessBasicInformation info,
        int length, out int returnLength);
}
