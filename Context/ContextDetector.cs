// ============================================================================
// RadialFlow - Detecção de contexto
//
// Duas etapas, ambas FORA da thread de UI (Task.Run):
//
//  1) PROBE RÁPIDO (Win32, microssegundos): WindowFromPoint + WM_NCHITTEST
//     + nome de classe. Resolve barra de título, campos de texto clássicos
//     e barra de tarefas sem tocar em UI Automation.
//
//  2) PROBE PROFUNDO (UI Automation, 20-150 ms): só quando o rápido não
//     basta, ou seja, Explorer/Desktop (item x fundo) e apps modernos
//     (navegador, Word...). Tem TIMEOUT: se estourar, usa o resultado
//     conservador do probe rápido, e a roda nunca fica esperando um app travado.
//
// O ponto analisado é SEMPRE a origem do gesto (onde o RMB foi pressionado),
// não a posição atual do cursor.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Automation;

namespace RadialFlow.Context;

public static class ContextDetector
{
    private readonly record struct ProbeResult(
        ContextInfo Info,        // resultado (ou fallback conservador se inconclusivo)
        bool Conclusive,         // true = não precisa de UI Automation
        bool IsShellView,        // root é Desktop ou janela do Explorer
        string RootClass);

    private static readonly HashSet<string> ClassicEditClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Edit", "RichEdit20W", "RichEdit20A", "RICHEDIT50W", "RichEditD2DPT", "Scintilla"
    };

    /// <summary>Detecta o contexto em (x, y), em pixels físicos de tela.</summary>
    public static Task<ContextInfo> DetectAsync(int x, int y, int timeoutMs = 200) =>
        Task.Run(async () =>
        {
            var probe = Probe(x, y);
            if (probe.Conclusive) return probe.Info;

            var deep = Task.Run(() => DeepProbe(probe, x, y));
            var finished = await Task.WhenAny(deep, Task.Delay(timeoutMs)).ConfigureAwait(false);
            return finished == deep ? deep.Result : probe.Info;
        });

    // ------------------------------------------------------------------
    // Etapa 1: probe rápido (Win32)
    // ------------------------------------------------------------------

    private static ProbeResult Probe(int x, int y)
    {
        IntPtr hit = WindowFromPoint(new NativePoint { X = x, Y = y });
        if (hit == IntPtr.Zero)
            return new(Make(RadialContextKind.Generic, IntPtr.Zero, x, y), true, false, "");

        IntPtr root = GetAncestor(hit, GA_ROOT);
        if (root == IntPtr.Zero) root = hit;
        string rootClass = ClassOf(root);

        // Barra de título: pergunta à janela raiz o que há naquele ponto.
        if (NcHitTest(root, x, y) == HTCAPTION)
            return new(Make(RadialContextKind.TitleBar, root, x, y), true, false, rootClass);

        // Barra de tarefas: sem contexto (evita probe inútil).
        if (rootClass is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
            return new(Make(RadialContextKind.Generic, root, x, y), true, false, rootClass);

        // Desktop / Explorer: só UI Automation sabe se é item ou fundo.
        // Fallback conservador (timeout): fundo.
        if (rootClass is "Progman" or "WorkerW" or "CabinetWClass")
            return new(Make(RadialContextKind.DesktopBackground, root, x, y), false, true, rootClass);

        // Campos de texto clássicos (Notepad, caixas de diálogo...).
        if (ClassicEditClasses.Contains(ClassOf(hit)))
            return new(Make(RadialContextKind.TextField, root, x, y), true, false, rootClass);

        // Qualquer outra coisa: UI Automation decide se é campo de texto.
        return new(Make(RadialContextKind.Generic, root, x, y), false, false, rootClass);
    }

    // ------------------------------------------------------------------
    // Etapa 2: probe profundo (UI Automation)
    // ------------------------------------------------------------------

    private static ContextInfo DeepProbe(ProbeResult probe, int x, int y)
    {
        try
        {
            var element = AutomationElement.FromPoint(new System.Windows.Point(x, y));
            if (element is null) return probe.Info;

            if (probe.IsShellView)
            {
                var item = FindItem(element);
                if (item is not null)
                {
                    var paths = ExplorerItemResolver.Resolve(item, probe.Info.RootWindow, probe.RootClass);
                    if (paths.Count > 0)
                        return probe.Info with { Kind = RadialContextKind.FileOrFolder, Paths = paths };
                }
                return probe.Info;   // fundo vazio
            }

            return IsEditable(element)
                ? probe.Info with { Kind = RadialContextKind.TextField }
                : probe.Info;
        }
        catch
        {
            // ElementNotAvailable, COMException, app fechou no meio... fica o fallback.
            return probe.Info;
        }
    }

    /// <summary>Sobe até 4 níveis procurando o ListItem/DataItem do arquivo.</summary>
    private static AutomationElement? FindItem(AutomationElement? element)
    {
        var walker = TreeWalker.ControlViewWalker;
        for (int i = 0; i < 4 && element is not null; i++)
        {
            var type = element.Current.ControlType;
            if (type == ControlType.ListItem || type == ControlType.DataItem) return element;
            if (type == ControlType.List || type == ControlType.Window || type == ControlType.Pane) return null;
            element = walker.GetParent(element);
        }
        return null;
    }

    /// <summary>Edit/Document editável. Documento somente leitura (ex.: página web) não conta.</summary>
    private static bool IsEditable(AutomationElement element)
    {
        var type = element.Current.ControlType;
        if (type != ControlType.Edit && type != ControlType.Document) return false;

        if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) &&
            ((ValuePattern)pattern).Current.IsReadOnly)
            return false;

        return element.Current.IsEnabled;
    }

    // ------------------------------------------------------------------
    // Helpers Win32
    // ------------------------------------------------------------------

    private static ContextInfo Make(RadialContextKind kind, IntPtr root, int x, int y) =>
        new(kind, root, root == IntPtr.Zero ? "" : ProcessNameOf(root), x, y, ContextInfo.NoPaths);

    internal static string ClassOf(IntPtr hwnd)
    {
        var sb = new StringBuilder(64);
        return GetClassName(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : "";
    }

    private static string ProcessNameOf(IntPtr hwnd)
    {
        try
        {
            GetWindowThreadProcessId(hwnd, out uint pid);
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch { return ""; }
    }

    /// <summary>WM_NCHITTEST com timeout: janela travada não trava a gente.</summary>
    private static int NcHitTest(IntPtr root, int x, int y)
    {
        // MAKELPARAM com coordenadas que podem ser negativas (multi-monitor).
        IntPtr lParam = (IntPtr)(nint)(((uint)y << 16) | ((uint)x & 0xFFFF));

        IntPtr ok = SendMessageTimeout(root, WM_NCHITTEST, IntPtr.Zero, lParam,
            SMTO_ABORTIFHUNG | SMTO_BLOCK, 50, out IntPtr result);

        return ok == IntPtr.Zero ? -1 : (int)result;
    }

    private const uint GA_ROOT = 2;
    private const uint WM_NCHITTEST = 0x0084;
    private const int HTCAPTION = 2;
    private const uint SMTO_BLOCK = 0x0001;
    private const uint SMTO_ABORTIFHUNG = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(NativePoint point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam,
        uint flags, uint timeout, out IntPtr result);
}
