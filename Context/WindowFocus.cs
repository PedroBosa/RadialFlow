// ============================================================================
// RadialFlow - Foco de janela sem efeitos colaterais
// ----------------------------------------------------------------------------
// Substitui o "truque do Alt" (keybd_event de Alt + SetForegroundWindow), que
// podia ativar a barra de menus do app alvo antes do Ctrl+V.
// Usa AttachThreadInput: nenhuma tecla é enviada.
// ============================================================================

using System;
using System.Runtime.InteropServices;

namespace RadialFlow.Context;

internal static class WindowFocus
{
    private const uint GA_ROOT = 2;
    private const int SW_RESTORE = 9;

    /// <summary>Traz a janela para o primeiro plano (melhor esforço).</summary>
    public static void ForceForeground(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;

        IntPtr current = GetForegroundWindow();
        if (current == hwnd) return;

        if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);

        uint foregroundThread = current == IntPtr.Zero ? 0 : GetWindowThreadProcessId(current, out _);
        uint thisThread = GetCurrentThreadId();

        bool attached = foregroundThread != 0 && foregroundThread != thisThread &&
                        AttachThreadInput(thisThread, foregroundThread, true);
        try
        {
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached) AttachThreadInput(thisThread, foregroundThread, false);
        }
    }

    /// <summary>True se a janela raiz em primeiro plano é a informada.</summary>
    public static bool IsForegroundRoot(IntPtr rootHwnd)
    {
        if (rootHwnd == IntPtr.Zero) return false;

        IntPtr foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero) return false;

        return foreground == rootHwnd || GetAncestor(foreground, GA_ROOT) == rootHwnd;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool fAttach);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
}
