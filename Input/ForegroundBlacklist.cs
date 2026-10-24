// ============================================================================
// RadialFlow - Módulo 4: Blacklist de aplicações
// ----------------------------------------------------------------------------
// Desativa o hook (MouseHookService.Enabled = false) quando a janela em
// primeiro plano pertence a um jogo, Blender, AutoCAD ou outro app 3D que
// depende do RMB para controle de câmera.
//
// Princípio fundamental: a decisão PRECISA estar pronta ANTES do RMB Down.
// Por isso, NÃO consultamos processos dentro do hook. Em vez disso:
//
//   1) SetWinEventHook(EVENT_SYSTEM_FOREGROUND) — callback a cada troca de
//      janela ativa — atualiza um cache com o nome do processo e o estado
//      de tela cheia exclusiva.
//
//   2) O campo volátil MouseHookService.Enabled é setado a partir do cache,
//      de modo que o hook só lê um bool: O(1), sem alocação, sem syscall.
//
// A lista configurável fica em BlacklistEntry[]. O Módulo 6 (configurações)
// salvará/carregará em JSON; por enquanto usamos defaults hardcoded.
//
// Detecção de tela cheia exclusiva (ex.: jogo em modo exclusivo):
//   * Compara o RECT da janela com o RECT do monitor. Se coincidem E a janela
//     não tem barra de título (WS_CAPTION), é tela cheia exclusiva.
//   * Quando BlockExclusiveFullscreen == true, o hook desabilita mesmo se o
//     processo não estiver na lista.
//
// NOTA sobre tela cheia "borderless" (Chrome F11, PowerPoint, players de
// vídeo): essas janelas removem WS_CAPTION e cobrem o monitor inteiro, sendo
// detectadas como "exclusiva". A propriedade BlockExclusiveFullscreen permite
// desativar globalmente. Para controle por app, adicione o processo à lista
// com FullscreenPolicy = Allow (o Módulo 6 vai expor isso na UI).
//
// Janelas do shell (Desktop, Taskbar) são IGNORADAS pela detecção de tela
// cheia: Progman, WorkerW, Shell_TrayWnd e Shell_SecondaryTrayWnd sempre
// cobrem o monitor e não devem desligar o hook.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using RadialFlow.Infrastructure;

namespace RadialFlow.Input;

public sealed class ForegroundBlacklist : IDisposable
{
    // ------------------------------------------------------------------
    // Configuração
    // ------------------------------------------------------------------

    /// <summary>
    /// Uma entrada na blacklist. Comparação por nome de processo (sem .exe),
    /// case-insensitive.
    /// </summary>
    public sealed record BlacklistEntry(string ProcessName, string Description);

    /// <summary>
    /// Lista padrão. O Módulo 6 (configurações) permitirá editá-la.
    /// Inclui apps 3D, modelagem e CAD. Launchers (Steam, Epic) NÃO entram:
    /// eles não usam RMB para câmera, e os jogos em si são pegos pela
    /// detecção de tela cheia exclusiva.
    /// </summary>
    public static List<BlacklistEntry> DefaultEntries { get; } = new()
    {
        // Apps 3D / modelagem
        new("blender",           "Blender"),
        new("3dsmax",            "3ds Max"),
        new("maya",              "Autodesk Maya"),
        new("acad",              "AutoCAD"),
        new("ZBrush",            "ZBrush"),
        new("SubstancePainter",  "Substance Painter"),
        new("SubstanceDesigner", "Substance Designer"),
        new("Cinema 4D",         "Cinema 4D"),
        new("Houdini",           "Houdini"),
        new("UnrealEditor",      "Unreal Editor"),
        new("Unity",             "Unity Editor"),
        new("Godot",             "Godot"),

        // CAD / engenharia
        new("SOLIDWORKS",        "SolidWorks"),
        new("CATIA",             "CATIA"),
        new("FreeCAD",           "FreeCAD"),
        new("fusion360",         "Fusion 360"),
        new("rhino",             "Rhino 3D"),
        new("revit",             "Revit"),
        new("SketchUp",          "SketchUp"),
    };

    /// <summary>
    /// Classes de janela do shell do Windows que devem ser IGNORADAS pela
    /// detecção de tela cheia exclusiva. Essas janelas sempre cobrem o monitor
    /// inteiro (Desktop, Taskbar) mas não são jogos/apps 3D.
    /// </summary>
    private static readonly HashSet<string> ShellClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman",                  // Desktop (ícones)
        "WorkerW",                  // Desktop (wallpaper/slideshow)
        "Shell_TrayWnd",            // Barra de tarefas principal
        "Shell_SecondaryTrayWnd"    // Barra de tarefas em monitores secundários
    };

    // ------------------------------------------------------------------
    // Estado
    // ------------------------------------------------------------------

    private readonly MouseHookService _hook;
    private IntPtr _winEventHook;

    // Delegate mantido em campo para evitar coleta pelo GC (mesma razão do hook de mouse).
    private readonly WinEventDelegate _winEventProc;

    // Cache: atualizado pelo callback de foreground (thread de UI do Windows).
    // Lido indiretamente pelo hook via MouseHookService.Enabled.
    private readonly HashSet<string> _blacklistedNames;

    /// <summary>
    /// Quando true, o hook é pausado manualmente pelo usuário (via bandeja).
    /// </summary>
    private bool _isPaused;
    public bool IsPaused
    {
        get => _isPaused;
        set
        {
            _isPaused = value;
            EvaluateForeground(GetForegroundWindow());
        }
    }

    /// <summary>
    /// Quando true, qualquer app em tela cheia exclusiva (mesmo fora da lista)
    /// desativa o hook. Padrão: true.
    ///
    /// NOTA: apps como Chrome em F11, PowerPoint em apresentação e players de
    /// vídeo em fullscreen borderless também são pegos. Para permitir o RadialFlow
    /// nesses cenários, desative esta propriedade ou adicione exceções por app
    /// (será implementado no Módulo 6).
    /// </summary>
    public bool BlockExclusiveFullscreen { get; set; } = true;

    // ------------------------------------------------------------------
    // Construção e ciclo de vida
    // ------------------------------------------------------------------

    public ForegroundBlacklist(MouseHookService hook, IEnumerable<BlacklistEntry>? entries = null)
    {
        _hook = hook;
        _winEventProc = OnForegroundChanged;

        // Monta o hashset de nomes (case-insensitive).
        _blacklistedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries ?? DefaultEntries)
            _blacklistedNames.Add(entry.ProcessName);
    }

    /// <summary>
    /// Instala o hook de eventos de janela. Chamar NA THREAD DE UI (precisa de
    /// message loop, o Dispatcher do WPF fornece).
    /// </summary>
    public void Start()
    {
        if (_winEventHook != IntPtr.Zero) return;

        _winEventHook = SetWinEventHook(
            EVENT_SYSTEM_FOREGROUND,    // eventMin
            EVENT_SYSTEM_FOREGROUND,    // eventMax
            IntPtr.Zero,                // hmodWinEventProc (0 = callback no nosso processo)
            _winEventProc,
            0,                          // idProcess (0 = todos)
            0,                          // idThread  (0 = todas)
            WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);

        if (_winEventHook == IntPtr.Zero)
        {
            AppLog.Warning("Blacklist", "Falha ao instalar SetWinEventHook.");
            return;
        }

        // Avalia a janela atual imediatamente para o estado inicial não depender
        // da primeira troca de foco.
        EvaluateForeground(GetForegroundWindow());
    }

    public void Stop()
    {
        if (_winEventHook == IntPtr.Zero) return;
        UnhookWinEvent(_winEventHook);
        _winEventHook = IntPtr.Zero;

        // Reativa o hook ao parar a blacklist (ex.: saída do app).
        _hook.Enabled = true;
    }

    public void Dispose() => Stop();

    // ------------------------------------------------------------------
    // Métodos de configuração em tempo de execução
    // ------------------------------------------------------------------

    /// <summary>Adiciona um processo à blacklist (efeito imediato).</summary>
    public void Add(string processName)
    {
        _blacklistedNames.Add(processName);
        // Reavalia caso a janela ativa JÁ seja esse processo.
        EvaluateForeground(GetForegroundWindow());
    }

    /// <summary>Remove um processo da blacklist (efeito imediato).</summary>
    public void Remove(string processName)
    {
        _blacklistedNames.Remove(processName);
        EvaluateForeground(GetForegroundWindow());
    }

    /// <summary>Substitui todas as entradas da blacklist e reavalia a janela ativa.</summary>
    public void UpdateEntries(IEnumerable<BlacklistEntry> entries)
    {
        _blacklistedNames.Clear();
        foreach (var entry in entries)
            _blacklistedNames.Add(entry.ProcessName);
        EvaluateForeground(GetForegroundWindow());
    }

    /// <summary>Retorna true se o processo está na blacklist.</summary>
    public bool Contains(string processName) =>
        _blacklistedNames.Contains(processName);

    // ------------------------------------------------------------------
    // Callback do evento de janela (roda na thread que tem o message loop)
    // ------------------------------------------------------------------

    /// <remarks>
    /// Callback nativo (SetWinEventHook): uma exceção aqui atravessaria código nativo e derrubaria o
    /// processo. Em caso de erro, registra e deixa o hook do mouse ATIVO (falha "aberta" e visível,
    /// em vez de desligar o RadialFlow em silêncio).
    /// </remarks>
    private void OnForegroundChanged(
        IntPtr hWinEventHook, uint eventType,
        IntPtr hwnd, int idObject, int idChild,
        uint dwEventThread, uint dwmsEventTime)
    {
        // idObject == OBJID_WINDOW (0) garante que é a janela em si, não um child.
        if (hwnd == IntPtr.Zero) return;

        try
        {
            EvaluateForeground(hwnd);
        }
        catch (Exception ex)
        {
            AppLog.Error("Blacklist", $"Falha ao avaliar a janela em primeiro plano", ex);
            _hook.Enabled = !_isPaused;
        }
    }

    /// <summary>
    /// Avalia se a janela em primeiro plano deve desativar o hook.
    /// Atualiza MouseHookService.Enabled.
    /// </summary>
    private void EvaluateForeground(IntPtr hwnd)
    {
        if (_isPaused)
        {
            _hook.Enabled = false;
            return;
        }

        if (hwnd == IntPtr.Zero)
        {
            _hook.Enabled = true;
            return;
        }

        // 1) Verifica se o nome do processo está na blacklist.
        string processName = GetProcessNameSafe(hwnd);
        if (_blacklistedNames.Contains(processName))
        {
            _hook.Enabled = false;
            AppLog.Debug("Blacklist", $"Desativado: processo '{processName}' está na blacklist.");
            return;
        }

        // 2) Verifica tela cheia exclusiva (jogo/player em modo exclusivo).
        //    IGNORA janelas do shell (Desktop, Taskbar) que sempre cobrem o monitor.
        if (BlockExclusiveFullscreen && IsExclusiveFullscreen(hwnd))
        {
            _hook.Enabled = false;
            AppLog.Debug("Blacklist", $"Desativado: '{processName}' em tela cheia exclusiva.");
            return;
        }

        // Nenhum critério atendido: hook ativo.
        _hook.Enabled = true;
    }

    // ------------------------------------------------------------------
    // Detecção de tela cheia exclusiva
    // ------------------------------------------------------------------

    /// <summary>
    /// Uma janela está em tela cheia exclusiva quando:
    ///   a) Seu RECT cobre 100% do monitor.
    ///   b) Não tem WS_CAPTION (sem barra de título).
    ///   c) NÃO é uma janela do shell (Desktop, Taskbar).
    ///
    /// Isso distingue jogos em fullscreen de apps maximizados (que TÊM caption).
    /// Navegadores em F11, PowerPoint e players borderless também são pegos
    /// (não têm caption e cobrem o monitor). Esse comportamento é DESEJÁVEL na
    /// maioria dos casos; pode ser desligado via BlockExclusiveFullscreen.
    /// </summary>
    private static bool IsExclusiveFullscreen(IntPtr hwnd)
    {
        // Ignora janelas do shell: Progman (Desktop), WorkerW (wallpaper),
        // Shell_TrayWnd (Taskbar). Elas cobrem o monitor inteiro mas não são
        // apps de tela cheia; sem esse filtro o hook desligaria no Desktop.
        string className = ClassOf(hwnd);
        if (ShellClasses.Contains(className))
            return false;

        // Obtém o estilo da janela.
        int style = GetWindowLong(hwnd, GWL_STYLE);

        // Janela com barra de título (maximizada normalmente) não é exclusiva.
        if ((style & WS_CAPTION) == WS_CAPTION)
            return false;

        // Compara o RECT da janela com o RECT do monitor.
        if (!GetWindowRect(hwnd, out RECT windowRect))
            return false;

        IntPtr monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        var monitorInfo = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref monitorInfo))
            return false;

        // Se a janela cobre todo o monitor (ou mais, por causa de borda invisível),
        // é tela cheia exclusiva.
        ref RECT mon = ref monitorInfo.rcMonitor;
        return windowRect.Left   <= mon.Left   &&
               windowRect.Top    <= mon.Top    &&
               windowRect.Right  >= mon.Right  &&
               windowRect.Bottom >= mon.Bottom;
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    /// <summary>
    /// Obtém o nome da classe da janela.
    /// </summary>
    private static string ClassOf(IntPtr hwnd)
    {
        var sb = new StringBuilder(64);
        return GetClassName(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : "";
    }

    /// <summary>
    /// Obtém o nome do processo dono de uma janela de forma segura.
    /// Retorna string vazia em caso de erro (processo saiu, acesso negado, etc.).
    /// </summary>
    private static string GetProcessNameSafe(IntPtr hwnd)
    {
        try
        {
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0) return "";

            // Tenta abrir o processo com acesso limitado (só queremos o nome).
            IntPtr hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (hProcess == IntPtr.Zero)
            {
                // Fallback via Process.GetProcessById (mais lento, pode lançar).
                using var proc = Process.GetProcessById((int)pid);
                return proc.ProcessName;
            }

            try
            {
                var sb = new StringBuilder(260);
                int capacity = sb.Capacity;
                if (QueryFullProcessImageName(hProcess, 0, sb, ref capacity))
                {
                    // Extrai só o nome do arquivo sem extensão.
                    string fullPath = sb.ToString();
                    int lastSlash = fullPath.LastIndexOf('\\');
                    string fileName = lastSlash >= 0 ? fullPath[(lastSlash + 1)..] : fullPath;
                    int dot = fileName.LastIndexOf('.');
                    return dot > 0 ? fileName[..dot] : fileName;
                }
            }
            finally
            {
                CloseHandle(hProcess);
            }
        }
        catch
        {
            // Processo saiu entre GetWindowThreadProcessId e OpenProcess.
        }

        return "";
    }

    // ------------------------------------------------------------------
    // P/Invoke
    // ------------------------------------------------------------------

    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT   = 0x0000;
    private const uint WINEVENT_SKIPOWNPROCESS  = 0x0002;

    private const int GWL_STYLE = -16;
    // WS_CAPTION = WS_BORDER | WS_DLGFRAME = 0x00C00000
    private const int WS_CAPTION = 0x00C00000;

    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    private delegate void WinEventDelegate(
        IntPtr hWinEventHook, uint eventType,
        IntPtr hwnd, int idObject, int idChild,
        uint dwEventThread, uint dwmsEventTime);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(
        uint eventMin, uint eventMax,
        IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc,
        uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref int lpdwSize);
}
