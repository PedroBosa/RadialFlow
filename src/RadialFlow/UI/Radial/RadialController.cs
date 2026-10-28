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
// ATALHO GLOBAL: RegisterHotKey (GlobalHotkeyService) abre a mesma roda no cursor.
// Como a janela não recebe input, o hook do mouse entra em HotkeyMode e entrega
// movimento/cliques: esquerdo escolhe, direito cancela. A pausa da bandeja
// desliga o atalho.
//
// THREADS: os eventos do hook chegam na thread do HOOK (dedicada). Os handlers
// só enfileiram no Dispatcher e voltam. Os campos tocados pelas duas threads
// (_lastPoint, _movePending) usam Volatile/Interlocked.
// _generation (só UI) invalida uma detecção em andamento: se o botão for solto
// antes de a detecção terminar, a roda NÃO aparece depois do fim do gesto.
// ============================================================================

using System;
using System.Collections.Generic;
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
    private readonly GlobalHotkeyService _hotkeys = new();
    private readonly Dictionary<string, (GlobalHotkeyService Service, CustomMenuProfile Profile)> _profileHotkeys = new(StringComparer.Ordinal);
    private readonly RadialMenuWindow _wheel = new();
    private readonly ForegroundBlacklist _blacklist;
    private readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;
    private WheelPreset _preset = WheelPreset.Standard;
    private HotkeyRegistrationError _hotkeyError;
    private bool _hotkeyWheelOpen;

    /// <summary>Pausa manual (bandeja). Também desliga o atalho global.</summary>
    public bool IsPaused
    {
        get => _blacklist.IsPaused;
        set
        {
            if (_blacklist.IsPaused == value) return;

            _blacklist.IsPaused = value;

            if (value)
            {
                _generation++;   // cancela uma preparação da roda pelo atalho em andamento
                _hotkeyWheelOpen = false;
                _hook.HotkeyMode = false;
                _wheel.Cancel();
                _hotkeyError = _hotkeys.SetEnabled(false);
                SetProfileHotkeysEnabled(false);
            }
            else
            {
                // Durante a gravação a combinação continua solta; EndHotkeyCapture religa depois.
                _hotkeyError = _hotkeys.SetEnabled(!_hotkeyCaptureActive);
                SetProfileHotkeysEnabled(!_hotkeyCaptureActive);
            }
        }
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

        _hotkeys.SetEnabled(!IsPaused);
        _hotkeyError = _hotkeys.TrySet(settings.Hotkey, out _);
        if (_hotkeyError is HotkeyRegistrationError.AlreadyInUse or HotkeyRegistrationError.Unavailable)
            AppLog.Warning("Hotkey", $"O atalho global não pôde ser ativado ({_hotkeyError}).");

        ApplyProfileHotkeys(settings);
    }

    /// <summary>
    /// (Re)registra o atalho de cada menu. Os serviços antigos são descartados: registrar de novo
    /// é a única forma de refletir remoções e trocas sem deixar combinações presas no sistema.
    /// </summary>
    private void ApplyProfileHotkeys(AppSettings settings)
    {
        foreach ((GlobalHotkeyService service, _) in _profileHotkeys.Values)
            service.Dispose();
        _profileHotkeys.Clear();

        foreach ((CustomMenuProfile profile, string hotkey) in
                 CustomMenuHotkeys.Resolve(settings.CustomProfiles, settings.Hotkey))
        {
            var service = new GlobalHotkeyService();
            HotkeyRegistrationError error = service.TrySet(hotkey, out _);
            if (error != HotkeyRegistrationError.None)
            {
                AppLog.Warning("Hotkey", $"O atalho do menu '{profile.Name}' não pôde ser ativado ({error}).");
                service.Dispose();
                continue;
            }

            string profileId = profile.Id;
            service.Pressed += () => OnProfileHotkey(profileId);
            if (IsPaused || _hotkeyCaptureActive) service.SetEnabled(false);

            _profileHotkeys[profileId] = (service, profile);
        }
    }

    private void SetProfileHotkeysEnabled(bool enabled)
    {
        foreach ((GlobalHotkeyService service, _) in _profileHotkeys.Values)
            service.SetEnabled(enabled);
    }

    /// <summary>Estado do registro do atalho (Configurações mostram um aviso quando não é None).</summary>
    internal HotkeyRegistrationError HotkeyError => _hotkeyError;

    internal bool IsHotkeyRegistered => _hotkeys.IsRegistered;

    internal string Hotkey => _hotkeys.Hotkey;

    /// <summary>
    /// Troca o atalho imediatamente (antes de salvar o arquivo). Em falha, o atalho anterior é restaurado.
    /// </summary>
    internal HotkeyRegistrationError TrySetHotkey(string? hotkey, out string normalized)
    {
        _hotkeyError = _hotkeys.TrySet(hotkey, out normalized);
        return _hotkeyError;
    }

    private bool _hotkeyCaptureActive;

    /// <summary>
    /// Gravação nas Configurações: solta a combinação atual para que ela própria possa ser capturada
    /// (com o atalho registrado, o Windows entrega WM_HOTKEY e a tecla nem chega à janela).
    /// </summary>
    internal void BeginHotkeyCapture()
    {
        if (_hotkeyCaptureActive || _disposed) return;

        _hotkeyCaptureActive = true;
        _hotkeyError = _hotkeys.SetEnabled(false);
        SetProfileHotkeysEnabled(false);
    }

    /// <summary>Fim da gravação: volta a valer o atalho desejado, respeitando a pausa da bandeja.</summary>
    internal void EndHotkeyCapture()
    {
        if (!_hotkeyCaptureActive) return;

        _hotkeyCaptureActive = false;
        if (_disposed) return;

        _hotkeyError = _hotkeys.SetEnabled(!IsPaused);
        SetProfileHotkeysEnabled(!IsPaused);
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
        _wheel.Hidden += OnWheelHidden;

        _hook.DragStarted += OnDragStarted;
        _hook.DragMoved += OnDragMoved;
        _hook.DragCompleted += OnDragCompleted;
        _hook.PointerMoved += OnDragMoved;   // mesmo coalescido do arrasto: só atualiza o destaque
        _hook.HotkeyClicked += OnHotkeyClicked;
        _hook.HotkeyCancelled += OnHotkeyCancelled;

        _hotkeys.Pressed += OnGlobalHotkey;

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

    // ------------------------------------------------------------------
    // Atalho global (thread de UI)
    // ------------------------------------------------------------------

    private void OnGlobalHotkey()
    {
        try
        {
            ToggleHotkeyWheel();
        }
        catch (Exception ex)
        {
            AppLog.Error("Hotkey", "Falha ao alternar a roda pelo atalho", ex);
        }
    }

    private void ToggleHotkeyWheel()
    {
        if (_disposed) return;

        if (_hotkeyWheelOpen)
        {
            _wheel.Cancel();   // Hidden limpa _hotkeyWheelOpen e HotkeyMode
            return;
        }

        // Não abre por cima de um gesto em andamento nem de uma roda que já está na tela.
        if (_hook.IsGestureActive || _wheel.IsVisible || IsPaused) return;
        if (!RadialMenuWindow.TryGetCursorPosition(out int x, out int y)) return;

        _ = BeginHotkeyAsync(x, y, profile: null);
    }

    private void OnProfileHotkey(string profileId)
    {
        try
        {
            ToggleProfileHotkey(profileId);
        }
        catch (Exception ex)
        {
            AppLog.Error("Hotkey", "Falha ao alternar a roda pelo atalho do menu", ex);
        }
    }

    /// <summary>Atalho de um menu: abre a roda com os itens dele sobre o contexto sob o cursor.</summary>
    private void ToggleProfileHotkey(string profileId)
    {
        if (_disposed) return;

        if (_hotkeyWheelOpen)
        {
            _wheel.Cancel();   // qualquer atalho fecha a roda aberta por atalho
            return;
        }

        if (_hook.IsGestureActive || _wheel.IsVisible || IsPaused) return;
        if (!_profileHotkeys.TryGetValue(profileId, out var entry)) return;
        if (!RadialMenuWindow.TryGetCursorPosition(out int x, out int y)) return;

        _ = BeginHotkeyAsync(x, y, entry.Profile);
    }

    private void OnHotkeyClicked(MouseHookService.POINT pt)
    {
        int x = pt.X, y = pt.Y;
        _ui.BeginInvoke(DispatcherPriority.Send, new Action(() => HandleHotkeyClick(x, y)));
    }

    private void HandleHotkeyClick(int x, int y)
    {
        if (_disposed || !_hotkeyWheelOpen) return;

        var (slice, stayedOpen) = _wheel.HandleHotkeyClick(x, y);
        if (!stayedOpen) _hotkeyWheelOpen = false;   // HideWheel também limpa pelo evento Hidden
        if (slice is not null) ExecuteSlice(slice);
    }

    private void OnHotkeyCancelled()
    {
        _ui.BeginInvoke(DispatcherPriority.Send, new Action(() =>
        {
            if (!_disposed && _hotkeyWheelOpen) _wheel.Cancel();
        }));
    }

    private void OnWheelHidden()
    {
        _hotkeyWheelOpen = false;
        _hook.HotkeyMode = false;
    }

    private async Task BeginHotkeyAsync(int x, int y, CustomMenuProfile? profile)
    {
        if (_disposed) return;
        int generation = ++_generation;

        try
        {
            (int cx, int cy) = RadialMenuWindow.PlaceOnScreen(x, y, _preset.Scale);

            var content = await BuildWheelContentAsync(x, y, cx, cy, generation, profile);
            if (content is null) return;

            // A preparação é assíncrona: um gesto pode ter começado (ou a pausa sido ligada) nesse meio-tempo.
            if (_hook.IsGestureActive || _wheel.IsVisible || IsPaused) return;

            // Do clique em diante o hook assume: a janela é NOACTIVATE e não recebe input.
            _hook.HotkeyMode = true;
            _wheel.Present(cx, cy, content.Value.Slices, content.Value.Backdrop, fromHotkey: true);

            if (!_wheel.IsVisible)
            {
                _hook.HotkeyMode = false;   // menos de duas fatias para este contexto
                return;
            }

            _hotkeyWheelOpen = true;

            if (cx != x || cy != y)
            {
                // Perto da borda: a roda fica no centro ajustado e o cursor vai junto, para não
                // nascer em cima de uma fatia que o usuário não escolheu.
                RadialMenuWindow.MoveCursor(cx, cy);
                x = cx;
                y = cy;
            }

            _wheel.UpdateCursor(x, y);
        }
        catch (Exception ex)
        {
            _hook.HotkeyMode = false;
            AppLog.Error("Hotkey", "Falha ao abrir a roda pelo atalho", ex);
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

            var content = await BuildWheelContentAsync(x, y, cx, cy, generation, profile: null);
            if (content is null) return;   // botão já foi solto ou o controller foi descartado

            _wheel.Present(cx, cy, content.Value.Slices, content.Value.Backdrop);

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

    /// <summary>
    /// Detecta o contexto e monta fatias + fundo desfocado. Devolve null se o gesto foi cancelado
    /// (<paramref name="generation"/> mudou) ou o controller foi descartado nesse meio-tempo.
    /// </summary>
    private async Task<(IReadOnlyList<RadialSlice> Slices, System.Windows.Media.Imaging.BitmapSource? Backdrop)?>
        BuildWheelContentAsync(int x, int y, int cx, int cy, int generation, CustomMenuProfile? profile)
    {
        int sizePx = (int)Math.Round(RadialMenuWindow.SizeDip * _preset.Scale * RadialMenuWindow.GetMonitorScale(cx, cy));
        Task<System.Windows.Media.Imaging.BitmapSource?> backdropTask = _preset.UsesHighContrast
            ? Task.FromResult<System.Windows.Media.Imaging.BitmapSource?>(null)
            : Task.Run(() => FrostedBackdrop.Create(cx, cy, sizePx));

        var context = await ContextDetector.DetectAsync(x, y);   // o contexto é o do ponto ORIGINAL do gesto
        if (generation != _generation || _disposed) return null;

        // Monta as fatias fora da UI: consulta disco/ferramentas (File.Exists, etc.).
        var slices = await Task.Run(() => profile is null
            ? SliceFactory.Build(context)
            : SliceFactory.BuildProfile(context, profile, SliceFactoryDependencies.Default));
        if (generation != _generation || _disposed) return null;

        // Normalmente já está pronto. Se atrasar, a roda aparece sem o vidro (tinta mais opaca) em vez de esperar.
        System.Windows.Media.Imaging.BitmapSource? backdrop =
            await Task.WhenAny(backdropTask, Task.Delay(80)) == backdropTask ? await backdropTask : null;
        if (generation != _generation || _disposed) return null;

        return (slices, backdrop);
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
    /// Ordem: WinEvent (UnhookWinEvent) → atalho global (UnregisterHotKey) → hook do mouse
    /// (UnhookWindowsHookEx, Join da thread) → janela da roda. Chamar na thread de UI; idempotente.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _hotkeyWheelOpen = false;
        _hook.HotkeyMode = false;

        _blacklist.Dispose();

        foreach ((GlobalHotkeyService service, _) in _profileHotkeys.Values)
            service.Dispose();
        _profileHotkeys.Clear();

        _hotkeys.Dispose();
        _hook.Dispose();    // encerra a thread do hook (Join)

        _hook.DragStarted -= OnDragStarted;
        _hook.DragMoved -= OnDragMoved;
        _hook.DragCompleted -= OnDragCompleted;
        _hook.PointerMoved -= OnDragMoved;
        _hook.HotkeyClicked -= OnHotkeyClicked;
        _hook.HotkeyCancelled -= OnHotkeyCancelled;
        _hotkeys.Pressed -= OnGlobalHotkey;

        _wheel.SliceActivated -= OnSliceActivated;
        _wheel.Hidden -= OnWheelHidden;

        _wheel.Close();
    }
}
