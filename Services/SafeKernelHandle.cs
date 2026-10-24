// ============================================================================
// RadialFlow - SafeHandle para objetos do kernel (processo, thread, job, token)
// ----------------------------------------------------------------------------
// Libera com CloseHandle de forma determinística (Dispose/using) e, se alguém
// esquecer, pelo finalizador crítico do SafeHandle. Usado pelo UnelevatedLauncher
// no lugar de IntPtr cru: nenhum handle de processo/thread/job/token depende de
// um bloco finally manual para não vazar.
// Handles de arquivo/pipe usam Microsoft.Win32.SafeHandles.SafeFileHandle.
// ============================================================================

using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RadialFlow.Services;

internal sealed class SafeKernelHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    /// <summary>Exigido pelo marshaller de P/Invoke (retornos e parâmetros <c>out</c>).</summary>
    public SafeKernelHandle() : base(ownsHandle: true) { }

    public SafeKernelHandle(IntPtr existingHandle, bool ownsHandle) : base(ownsHandle) => SetHandle(existingHandle);

    protected override bool ReleaseHandle() => CloseHandle(handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
