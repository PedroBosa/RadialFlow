// ============================================================================
// RadialFlow - Controller
//
// Fluxo:
//   DragStarted   -> detecta o contexto na ORIGEM do gesto (async, fora da UI)
//                    -> monta as fatias (também fora da UI) -> mostra a roda
//   DragMoved     -> destaca a fatia (coalescido)
//   DragCompleted -> fecha a roda e executa a fatia sob o cursor
//   SliceActivated (teclado) -> a roda já se fechou; executa a fatia escolhida
//
// THREADS: os eventos do hook chegam na thread do HOOK (dedicada). Os handlers
// só enfileiram no Dispatcher e voltam. Os campos tocados pelas duas threads
// (_lastPoint, _movePending) usam Volatile/Interlocked.
// _generation (só UI) invalida uma detecção em andamento: se o botão for solto
// antes de a detecção terminar, a roda NÃO aparece depois do fim do gesto.
// ============================================================================

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using RadialFlow.Infrastructure;
using RadialFlow.Config;
using RadialFlow.Context;
using RadialFlow.Input;

namespace RadialFlow.UI;

public sealed class RadialController : IDisposable
{
    private readonly MouseHookService _hook = new();
    private readonly RadialMenuWindow _wheel = new();
    private readonly ForegroundBlacklist _blacklist;
    private readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;
    private WheelPreset _preset = WheelPreset.Standard;

    /// <summary>Pausa manual (bandeja).</summary>
    public bool IsPaused
    {
        get => _blacklist.IsPaused;
        set => _blacklist.IsPaused = value;
    }

    public void ApplySettings(AppSettings settings)
    {
        _hook.DeadzonePixels = settings.DeadzonePixels;
        _wheel.DwellMilliseconds = settings.DwellMilliseconds;
        _wheel.AccentName = settings.Accent;
        _preset = WheelPreset.Resolve(settings.WheelPreset);
        _wheel.Preset = _preset;
        _blacklist.BlockExclusiveFullscreen = settings.BlockExclusiveFullscreen;
        _blacklist.UpdateEntries(settings.Blacklist);
    }

    // Compartilhados entre a thread do hook e a UI.
    private readonly Action _flushMove;
    private long _lastPoint;     // x no alto, y embaixo (um único valor => leitura atômica)
    private int _movePending;    // 0/1

    // Somente UI.
    private int _generation;
    private bool _started;
    private bool _disposed;

    public RadialController()
    {
        _flushMove = FlushMove;   // delegate criado uma vez só
        _blacklist = new ForegroundBlacklist(_hook);
    }

    /// <summary>Instala os hooks e liga os eventos. Chamar na thread de UI; idempotente.</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started) return;
        _started = true;

        _wheel.Prewarm();
        _wheel.EnableKeyboardNavigation();
        _wheel.SliceActivated += OnSliceActivated;

        _hook.DragStarted += OnDragStarted;
        _hook.DragMoved += OnDragMoved;
        _hook.DragCompleted += OnDragCompleted;

        _hook.Start();         // sobe a thread dedicada do hook
        _blacklist.Start();    // SetWinEventHook: precisa do message loop da UI
    }

    // ------------------------------------------------------------------
    // Handlers (thread do HOOK): só enfileiram e retornam (< 5 ms)
    // ------------------------------------------------------------------

    private void OnDragStarted(MouseHookService.POINT origin)
    {
        int x = origin.X, y = origin.Y;
        _ui.BeginInvoke(DispatcherPriority.Send, new Action(() => _ = BeginGestureAsync(x, y)));
    }

    private void OnDragMoved(MouseHookService.POINT pt)
    {
        Volatile.Write(ref _lastPoint, Pack(pt.X, pt.Y));
        if (Interlocked.Exchange(ref _movePending, 1) == 0)
            _ui.BeginInvoke(DispatcherPriority.Send, _flushMove);
    }

    private void OnDragCompleted(MouseHookService.POINT pt)
    {
        int x = pt.X, y = pt.Y;
        _ui.BeginInvoke(DispatcherPriority.Send, new Action(() => CompleteGesture(x, y)));
    }

    // ------------------------------------------------------------------
    // Thread de UI
    // ------------------------------------------------------------------

    /// <summary>
    /// Fecha a roda e executa a fatia sob o cursor. Exceção de uma ação não pode derrubar o app
    /// (subiria para o Dispatcher): é registrada aqui.
    /// </summary>
    private void CompleteGesture(int x, int y)
    {
        if (_disposed) return;

        _generation++;                               // cancela detecção em andamento
        Interlocked.Exchange(ref _movePending, 0);

        ExecuteSlice(_wheel.Dismiss(x, y));
    }

    /// <summary>Enter ou número pressionado com a roda aberta: a roda já escondeu; só falta executar.</summary>
    private void OnSliceActivated(RadialSlice slice)
    {
        if (_disposed) return;

        _generation++;
        Interlocked.Exchange(ref _movePending, 0);

        ExecuteSlice(slice);
    }

    /// <summary>Exceção de uma ação não pode derrubar o app (subiria para o Dispatcher): é registrada aqui.</summary>
    private static void ExecuteSlice(RadialSlice? slice)
    {
        try
        {
            slice?.Execute?.Invoke();
        }
        catch (Exception ex)
        {
            AppLog.Error("Controller", $"Falha ao executar a fatia", ex);
        }
    }

    private async Task BeginGestureAsync(int x, int y)
    {
        if (_disposed) return;
        int generation = ++_generation;

        try
        {
            // Perto de uma borda a roda é deslocada para caber na tela. O fundo de vidro é capturado nesse centro
            // FINAL, em paralelo com a detecção de contexto (que leva dezenas de ms): não soma atraso.
            (int cx, int cy) = RadialMenuWindow.PlaceOnScreen(x, y, _preset.Scale);
            int sizePx = (int)Math.Round(RadialMenuWindow.SizeDip * _preset.Scale * RadialMenuWindow.GetMonitorScale(cx, cy));
            Task<System.Windows.Media.Imaging.BitmapSource?> backdropTask = _preset.UsesHighContrast
                ? Task.FromResult<System.Windows.Media.Imaging.BitmapSource?>(null)
                : Task.Run(() => FrostedBackdrop.Create(cx, cy, sizePx));

            var context = await ContextDetector.DetectAsync(x, y);   // o contexto é o do ponto ORIGINAL do gesto
            if (generation != _generation || _disposed) return;   // botão já foi solto

            // Monta as fatias fora da UI: consulta disco/ferramentas (File.Exists, etc.).
            var slices = await Task.Run(() => SliceFactory.Build(context));
            if (generation != _generation || _disposed) return;

            // Normalmente já está pronto. Se atrasar, a roda aparece sem o vidro (tinta mais opaca) em vez de esperar.
            System.Windows.Media.Imaging.BitmapSource? backdrop = await Task.WhenAny(backdropTask, Task.Delay(80)) == backdropTask
                ? await backdropTask
                : null;
            if (generation != _generation || _disposed) return;

            _wheel.Present(cx, cy, slices, backdrop);

            if (cx != x || cy != y)
            {
                // Roda deslocada: leva o cursor ao novo centro. Sem isso ele nasceria pairando sobre uma fatia
                // e soltar o botão executaria uma ação que o usuário não escolheu.
                RadialMenuWindow.MoveCursor(cx, cy);
                Volatile.Write(ref _lastPoint, Pack(cx, cy));
            }

            var (lx, ly) = Unpack(Volatile.Read(ref _lastPoint));
            _wheel.UpdateCursor(lx, ly);             // destaca já na posição atual
        }
        catch (Exception ex)
        {
            AppLog.Error("Controller", $"Falha ao iniciar o gesto", ex);
        }
    }

    private void FlushMove()
    {
        Interlocked.Exchange(ref _movePending, 0);
        if (_disposed) return;

        try
        {
            var (x, y) = Unpack(Volatile.Read(ref _lastPoint));
            _wheel.UpdateCursor(x, y);
        }
        catch (Exception ex)
        {
            AppLog.Error("Controller", $"Falha ao atualizar a roda", ex);
        }
    }

    private static long Pack(int x, int y) => ((long)x << 32) | (uint)y;
    private static (int X, int Y) Unpack(long v) => ((int)(v >> 32), (int)(v & 0xFFFFFFFF));

    /// <summary>
    /// Ordem: WinEvent (UnhookWinEvent) → hook do mouse (UnhookWindowsHookEx, Join da thread) →
    /// janela da roda. Chamar na thread de UI; idempotente.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _blacklist.Dispose();
        _hook.Dispose();    // encerra a thread do hook (Join)

        _hook.DragStarted -= OnDragStarted;
        _hook.DragMoved -= OnDragMoved;
        _hook.DragCompleted -= OnDragCompleted;
        _wheel.SliceActivated -= OnSliceActivated;

        _wheel.Close();
    }
}
