// ============================================================================
// RadialFlow - Hook global do mouse e zona morta do gesto
// ----------------------------------------------------------------------------
// MUDANÇA IMPORTANTE: o hook agora roda em uma THREAD DEDICADA, com message
// loop próprio. Antes ele vivia na thread de UI do WPF: qualquer bloqueio ali
// (Clipboard com retries, File.Exists em rede, diálogos, construção da roda)
// atrasava o mouse do SISTEMA INTEIRO e, se estourasse o limite do Windows
// (LowLevelHooksTimeout), o hook era REMOVIDO em silêncio.
//
// Consequências:
//   * Os eventos DragStarted / DragMoved / DragCompleted são disparados NA
//     THREAD DO HOOK. Os handlers devem apenas enfileirar trabalho no
//     Dispatcher e retornar (o RadialController já faz isso).
//   * Estado do gesto (_state, _origin) pertence só à thread do hook.
//   * Enabled e a deadzone são lidos pelo hook e escritos pela UI: usam
//     volatile / Volatile.
//   * WATCHDOG: a cada 10 s, com o mouse em repouso (estado Idle), o hook é
//     reinstalado. Cobre o caso de o Windows ter removido o hook em silêncio.
//
// Lógica do gesto:
//   RMB Down  -> engole; começa a rastrear (Pending).
//   Move      -> distância² > deadzone² => Dragging (DragStarted + DragMoved).
//   RMB Up    -> Dragging: DragCompleted e engole (sem menu nativo).
//                Pending : reinjeta Down+Up (SendInput com assinatura) => o
//                          menu nativo aparece normalmente.
// ============================================================================

using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using RadialFlow.Infrastructure;

namespace RadialFlow.Input;

public sealed unsafe class MouseHookService : IDisposable
{
    // ------------------------------------------------------------------
    // Configuração
    // ------------------------------------------------------------------

    private int _deadzonePixels = 15;
    private int _deadzoneSquared = 15 * 15;   // lido pela thread do hook via Volatile

    /// <summary>Distância mínima (px) para ativar a roda. Mínimo 5.</summary>
    public int DeadzonePixels
    {
        get => _deadzonePixels;
        set
        {
            _deadzonePixels = Math.Max(5, value);
            Volatile.Write(ref _deadzoneSquared, _deadzonePixels * _deadzonePixels);
        }
    }

    /// <summary>Assinatura nos cliques que NÓS injetamos (evita loop no hook).</summary>
    private static readonly nuint ReplaySignature = 0x52464C57; // "RFLW"

    /// <summary>Intervalo do watchdog que reinstala o hook.</summary>
    private const uint WatchdogIntervalMs = 10_000;

    /// <summary>
    /// Chave geral (blacklist, pausa). Leitura O(1) sem alocação no hook.
    /// </summary>
    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    private volatile bool _enabled = true;

    // ------------------------------------------------------------------
    // Eventos (disparados na thread do hook; coordenadas em pixels físicos)
    // ------------------------------------------------------------------

    public event Action<POINT>? DragStarted;
    public event Action<POINT>? DragMoved;
    public event Action<POINT>? DragCompleted;

    // ------------------------------------------------------------------
    // Estado (somente a thread do hook toca nestes campos)
    // ------------------------------------------------------------------

    private enum GestureState : byte { Idle, Pending, Dragging }

    private GestureState _state = GestureState.Idle;
    private POINT _origin;
    private IntPtr _hookId = IntPtr.Zero;

    // CRÍTICO: delegate em campo, senão o GC o coleta e o app crasha.
    private readonly LowLevelMouseProc _proc;

    // Buffer pré-alocado para SendInput (zero alocação no hook).
    private readonly INPUT[] _replayBuffer = new INPUT[2];
    private readonly int _inputSize = Marshal.SizeOf<INPUT>();

    // Thread dedicada
    private Thread? _thread;
    private uint _threadId;
    private readonly ManualResetEventSlim _ready = new(false);
    private Exception? _startError;
    private IntPtr _moduleHandle;   // calculado uma vez na thread do hook (sem alocação no watchdog)
    private bool _disposed;

    public MouseHookService()
    {
        _proc = HookCallback;
        _replayBuffer[0] = BuildRightClick(MOUSEEVENTF_RIGHTDOWN);
        _replayBuffer[1] = BuildRightClick(MOUSEEVENTF_RIGHTUP);
    }

    // ------------------------------------------------------------------
    // Ciclo de vida
    // ------------------------------------------------------------------

    /// <summary>
    /// Sobe a thread dedicada e espera o hook ser instalado.
    /// </summary>
    /// <exception cref="ObjectDisposedException">Serviço já descartado.</exception>
    /// <exception cref="Win32Exception">O Windows recusou o hook.</exception>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_thread is not null) return;

        _ready.Reset();
        _startError = null;

        _thread = new Thread(HookThreadMain)
        {
            Name = "RadialFlow.MouseHook",
            IsBackground = true,
            Priority = ThreadPriority.Highest   // o mouse do sistema depende desta thread
        };
        _thread.Start();

        _ready.Wait();   // espera a instalação (ou falha) do hook

        if (_startError is not null)
        {
            _thread.Join(1000);
            _thread = null;
            _threadId = 0;
            throw _startError;
        }
    }

    /// <summary>
    /// Encerra o message loop da thread do hook, que chama <c>UnhookWindowsHookEx</c> e
    /// <c>KillTimer</c> antes de sair (o hook pertence àquela thread). Idempotente.
    /// </summary>
    public void Stop()
    {
        var thread = _thread;
        if (thread is null) return;

        if (_threadId != 0)
            PostThreadMessage(_threadId, WM_QUIT, UIntPtr.Zero, IntPtr.Zero);

        if (!thread.Join(3000))
            AppLog.Warning("Hook", "A thread do hook não encerrou no prazo.");

        _thread = null;
        _threadId = 0;
    }

    /// <summary>Desinstala o hook (via <see cref="Stop"/>) e libera o evento de sincronização.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Stop();
        _ready.Dispose();
    }

    // ------------------------------------------------------------------
    // Thread do hook: instala, bombeia mensagens, reinstala (watchdog)
    // ------------------------------------------------------------------

    private void HookThreadMain()
    {
        // Nada pode escapar daqui: exceção numa thread própria derruba o processo inteiro.
        try
        {
            _threadId = GetCurrentThreadId();

            // Garante que a fila de mensagens da thread exista antes de qualquer PostThreadMessage.
            PeekMessage(out _, IntPtr.Zero, WM_USER, WM_USER, PM_NOREMOVE);

            using (var process = Process.GetCurrentProcess())
            using (var module = process.MainModule
                                ?? throw new InvalidOperationException("Módulo principal indisponível."))
            {
                _moduleHandle = GetModuleHandle(module.ModuleName);
            }

            _hookId = InstallHook();
            if (_hookId == IntPtr.Zero)
            {
                _startError = new Win32Exception(Marshal.GetLastWin32Error(),
                    "Falha ao instalar o hook global do mouse.");
            }
        }
        catch (Exception ex)
        {
            _startError = new InvalidOperationException("Falha ao preparar o hook global do mouse.", ex);
        }
        finally
        {
            _ready.Set();
        }

        if (_startError is not null) return;

        // Timer de thread (hWnd nulo): gera WM_TIMER nesta fila.
        nuint timerId = SetTimer(IntPtr.Zero, 0, WatchdogIntervalMs, IntPtr.Zero);

        try
        {
            while (GetMessage(out MSG msg, IntPtr.Zero, 0, 0) > 0)
            {
                if (msg.message == WM_TIMER)
                {
                    RefreshHook();
                    continue;
                }

                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Hook", $"Message loop do hook falhou", ex);
        }
        finally
        {
            if (timerId != 0) KillTimer(IntPtr.Zero, timerId);

            if (_hookId != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hookId);
                _hookId = IntPtr.Zero;
            }
            _state = GestureState.Idle;
        }
    }

    private IntPtr InstallHook() => SetWindowsHookEx(WH_MOUSE_LL, _proc, _moduleHandle, 0);

    /// <summary>
    /// Reinstala o hook (nunca no meio de um gesto). Entre InstallHook e
    /// Unhook esta thread não bombeia mensagens, logo nenhum evento chega
    /// aos dois hooks ao mesmo tempo.
    /// </summary>
    private void RefreshHook()
    {
        if (_state != GestureState.Idle) return;

        IntPtr fresh = InstallHook();
        if (fresh == IntPtr.Zero)
        {
            AppLog.Warning("Hook", $"Watchdog: reinstalação falhou (erro {Marshal.GetLastWin32Error()}); mantendo o hook atual.");
            return;
        }

        IntPtr old = _hookId;
        _hookId = fresh;
        if (old != IntPtr.Zero) UnhookWindowsHookEx(old);
    }

    // ------------------------------------------------------------------
    // HookCallback (O(1), sem alocação, sem sqrt)
    // ------------------------------------------------------------------

    /// <summary>
    /// Callback nativo. Uma exceção aqui atravessaria código nativo e derrubaria o processo:
    /// é registrada e o evento segue para o próximo hook (o mouse do sistema nunca trava).
    /// </summary>
    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0 && HandleMouseEvent(wParam, lParam))
                return (IntPtr)1;   // engolido
        }
        catch (Exception ex)
        {
            _state = GestureState.Idle;
            AppLog.Error("Hook", $"Callback falhou", ex);
        }

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    /// <summary>Processa um evento. Retorna true para engolir o evento.</summary>
    private bool HandleMouseEvent(IntPtr wParam, IntPtr lParam)
    {
        var data = (MSLLHOOKSTRUCT*)lParam;

        // Eventos que nós mesmos injetamos (replay do clique) passam direto.
        if (data->dwExtraInfo == ReplaySignature) return false;

        switch (unchecked((int)wParam.ToInt64()))
        {
            case WM_RBUTTONDOWN:
                if (!Enabled) return false;   // app em blacklist / pausado: passa direto

                _origin = data->pt;
                _state = GestureState.Pending;
                return true;

            case WM_MOUSEMOVE:
                if (_state == GestureState.Pending)
                {
                    int dx = data->pt.X - _origin.X;
                    int dy = data->pt.Y - _origin.Y;

                    if (dx * dx + dy * dy > Volatile.Read(ref _deadzoneSquared))
                    {
                        _state = GestureState.Dragging;
                        Raise(DragStarted, _origin);
                        Raise(DragMoved, data->pt);
                    }
                }
                else if (_state == GestureState.Dragging)
                {
                    Raise(DragMoved, data->pt);
                }
                return false;

            case WM_RBUTTONUP:
                if (_state == GestureState.Dragging)
                {
                    _state = GestureState.Idle;
                    Raise(DragCompleted, data->pt);
                    return true;   // sem menu nativo
                }

                if (_state == GestureState.Pending)
                {
                    _state = GestureState.Idle;
                    SendInput(2, _replayBuffer, _inputSize);   // clique normal: menu nativo
                    return true;
                }
                return false;
        }

        return false;
    }

    /// <summary>Uma exceção em handler dentro de callback nativo derrubaria o processo.</summary>
    private static void Raise(Action<POINT>? handler, POINT point)
    {
        try { handler?.Invoke(point); }
        catch (Exception ex) { AppLog.Error("Hook", $"Handler falhou", ex); }
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static INPUT BuildRightClick(uint flag) => new()
    {
        type = INPUT_MOUSE,
        mi = new MOUSEINPUT
        {
            dx = 0,
            dy = 0,
            mouseData = 0,
            dwFlags = flag,
            time = 0,
            dwExtraInfo = ReplaySignature
        }
    };

    // ------------------------------------------------------------------
    // P/Invoke
    // ------------------------------------------------------------------

    private const int WH_MOUSE_LL = 14;
    private const int WM_MOUSEMOVE = 0x0200;
    private const int WM_RBUTTONDOWN = 0x0204;
    private const int WM_RBUTTONUP = 0x0205;

    private const uint WM_USER = 0x0400;
    private const uint WM_QUIT = 0x0012;
    private const uint WM_TIMER = 0x0113;
    private const uint PM_NOREMOVE = 0x0000;

    private const uint INPUT_MOUSE = 0;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public UIntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public nuint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public nuint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public MOUSEINPUT mi;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, [In] INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint idThread, uint msg, UIntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern nuint SetTimer(IntPtr hWnd, nuint nIDEvent, uint uElapse, IntPtr lpTimerFunc);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool KillTimer(IntPtr hWnd, nuint uIDEvent);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);
}
