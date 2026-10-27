// ============================================================================
// RadialFlow - Menu radial (janela overlay)
// ----------------------------------------------------------------------------
// Anel contínuo de fatias claras e translúcidas (estilo pie menu do macOS):
// a fatia sob o cursor fica azul. O desenho das fatias está em
// RadialMenuWindow.Visuals.cs; aqui ficam a janela Win32, o hit-test, o hover
// e a navegação por submenus.
//
// SUBMENUS: pairar sobre uma fatia com opções (ex.: "Converter") por
// DwellMilliseconds abre o submenu; o traço azul na borda da fatia mostra o
// tempo restante. Voltar ao centro pelo mesmo tempo retorna ao menu anterior;
// ali, um círculo no mesmo estilo contorna o miolo enquanto o tempo corre.
//
// TECLADO: com a roda aberta, 1–8 escolhem, setas circulam, Enter abre/executa
// e Esc volta ou fecha (atalhos só enquanto visível; ver
// RadialMenuWindow.Keyboard.cs).
// ============================================================================

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace RadialFlow.UI;

public partial class RadialMenuWindow : Window
{
    /// <summary>Tempo padrão de espera para abrir um submenu (antes: 260 ms, rápido demais).</summary>
    public const int DefaultDwellMilliseconds = 650;
    public const int MinDwellMilliseconds = 200;
    public const int MaxDwellMilliseconds = 2000;

    private IReadOnlyList<RadialSlice> _currentSlices = Array.Empty<RadialSlice>();
    private readonly Stack<(IReadOnlyList<RadialSlice> Slices, int ParentIndex, string ParentTitle)> _navHistory = new();

    private IntPtr _hwnd;
    private double _scale = 1.0;
    private double _layoutScale = 1.0;
    private int _originX, _originY;
    private int _cursorX, _cursorY;
    private int _hover = -1;

    private readonly DispatcherTimer _dwellTimer;
    private readonly DispatcherTimer _backDwellTimer;
    private int _dwellMilliseconds = DefaultDwellMilliseconds;
    private int _dwellTargetIndex = -1;

    public RadialMenuWindow()
    {
        InitializeComponent();

        _dwellTimer = new DispatcherTimer { Interval = DwellDuration };
        _dwellTimer.Tick += OnDwellTimerTick;

        _backDwellTimer = new DispatcherTimer { Interval = DwellDuration };
        _backDwellTimer.Tick += OnBackDwellTimerTick;
    }

    /// <summary>
    /// Quanto tempo o cursor precisa ficar sobre uma fatia (ou no centro, para voltar) até o submenu
    /// abrir. Limitado a [<see cref="MinDwellMilliseconds"/>, <see cref="MaxDwellMilliseconds"/>].
    /// </summary>
    public int DwellMilliseconds
    {
        get => _dwellMilliseconds;
        set
        {
            _dwellMilliseconds = Math.Clamp(value, MinDwellMilliseconds, MaxDwellMilliseconds);
            _dwellTimer.Interval = DwellDuration;
            _backDwellTimer.Interval = DwellDuration;
        }
    }

    /// <summary>Cor de destaque escolhida nas Configurações (ver <see cref="RadialTheme.AccentChoices"/>).</summary>
    public string AccentName { get; set; } = "Blue";

    /// <summary>Estilo visual (tamanho e contraste) aplicado na próxima abertura da roda.</summary>
    internal WheelPreset Preset { get; set; } = WheelPreset.Standard;

    /// <summary>Tema fixo (testes e pré-visualização); nulo = segue o Windows a cada abertura.</summary>
    internal RadialTheme? ThemeOverride { get; set; }

    private TimeSpan DwellDuration => TimeSpan.FromMilliseconds(_dwellMilliseconds);

    // ------------------------------------------------------------------
    // Ciclo de vida Win32
    // ------------------------------------------------------------------

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;

        // Sem WS_EX_TRANSPARENT: enquanto a roda está aberta, o mouse fica NELA, e não no programa de baixo.
        // Antes, o cursor parado numa opção era, para o Explorer, o cursor parado sobre um arquivo, e a dica dele
        // ("Tipo de item, Tamanho...") aparecia por cima da roda. NOACTIVATE continua: a roda nunca rouba o foco,
        // nem quando clicada. O hook do mouse não é afetado (ele vê os eventos antes de qualquer janela).
        int ex = GetWindowLong(_hwnd, GWL_EXSTYLE);
        SetWindowLong(_hwnd, GWL_EXSTYLE, (ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW) & ~WS_EX_TRANSPARENT);

        AttachHotkeyHandler();   // recebe WM_HOTKEY sem precisar de foco
    }

    public void Prewarm()
    {
        Left = -32000;
        Top = -32000;
        Show();
        Hide();
    }

    /// <summary>Para os DispatcherTimers: o Dispatcher mantém timers ativos vivos (e a janela junto).</summary>
    protected override void OnClosed(EventArgs e)
    {
        _dwellTimer.Stop();
        _backDwellTimer.Stop();
        _dwellTimer.Tick -= OnDwellTimerTick;
        _backDwellTimer.Tick -= OnBackDwellTimerTick;
        base.OnClosed(e);
    }

    // ------------------------------------------------------------------
    // API pública
    // ------------------------------------------------------------------

    /// <param name="backdrop">Fundo já desfocado (<see cref="FrostedBackdrop"/>), no tamanho da roda; null usa tinta mais opaca.</param>
    public void Present(int originX, int originY, IReadOnlyList<RadialSlice> slices,
        System.Windows.Media.Imaging.BitmapSource? backdrop = null)
    {
        if (slices.Count < RadialGeometry.MinSlices) return;

        _navHistory.Clear();
        ResetKeyboardState();
        _currentSlices = RadialSlicePaging.Paginate(slices);

        _originX = originX;
        _originY = originY;
        _cursorX = originX;
        _cursorY = originY;
        _scale = GetMonitorScale(originX, originY);

        WheelPreset preset = Preset;
        _layoutScale = preset.Scale;
        Root.LayoutTransform = _layoutScale == 1.0
            ? Transform.Identity
            : new ScaleTransform(_layoutScale, _layoutScale);

        ApplyTheme(ThemeOverride ?? RadialTheme.Create(
            RadialTheme.ReadLightTheme(), RadialTheme.ResolveAccent(AccentName), preset.UsesHighContrast));
        ApplyBackdrop(preset.UsesHighContrast ? null : backdrop);   // alto contraste não usa o fundo desfocado
        BuildSegments();
        ShowHub(null);
        HideHint();

        int px = (int)Math.Round(SizeDip * _layoutScale * _scale);
        SetWindowPos(_hwnd, HWND_TOPMOST, originX - px / 2, originY - px / 2, px, px, SWP_NOACTIVATE);

        Show();
        RegisterHotkeys();   // só enquanto a roda está na tela
        PlayAppearAnimation();
    }

    public void UpdateCursor(int screenX, int screenY)
    {
        if (!IsVisible) return;

        _cursorX = screenX;
        _cursorY = screenY;

        if (_keyboardMode)
        {
            // Tremores do mouse não tiram a seleção feita pelo teclado; um movimento de verdade devolve o controle.
            double dx = screenX - _keyboardAnchorX;
            double dy = screenY - _keyboardAnchorY;
            double tolerance = 4 * _scale;
            if (dx * dx + dy * dy <= tolerance * tolerance) return;

            ResetKeyboardState();
        }

        SetHover(HitTestScreen(screenX, screenY));
    }

    /// <summary>
    /// Recalcula o destaque na última posição conhecida do cursor. Necessário depois de trocar de menu:
    /// com o mouse parado não chega evento novo, e o destaque ficaria apagado (e o soltar do botão
    /// executaria uma opção que não aparece selecionada).
    /// </summary>
    private void RefreshHover()
    {
        _hover = int.MinValue;   // força a reavaliação mesmo se o índice calculado for igual ao anterior
        SetHover(HitTestScreen(_cursorX, _cursorY));
    }

    /// <summary>
    /// Fecha a roda e devolve a fatia escolhida: a do teclado, se ele estiver no comando; senão, a que está
    /// sob o cursor no momento do soltar.
    /// </summary>
    public RadialSlice? Dismiss(int screenX, int screenY)
    {
        if (!IsVisible) return null;

        int index = _keyboardMode ? _keyboardIndex : HitTestScreen(screenX, screenY);
        RadialSlice? result = index >= 0 && index < _currentSlices.Count ? _currentSlices[index] : null;

        HideWheel();
        return result;
    }

    /// <summary>Fecha a roda sem executar nada (Esc no menu principal).</summary>
    internal void Cancel()
    {
        if (IsVisible) HideWheel();
    }

    private void HideWheel()
    {
        _dwellTimer.Stop();
        _backDwellTimer.Stop();
        HideAllDwellArcs();
        UnregisterHotkeys();

        Root.BeginAnimation(OpacityProperty, null);
        RootScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        RootScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        Root.Opacity = 0;
        RootScale.ScaleX = RootScale.ScaleY = 0.75;

        Hide();
        _hover = -1;
        ResetKeyboardState();
        _navHistory.Clear();
    }

    // ------------------------------------------------------------------
    // Hit-test polar
    // ------------------------------------------------------------------

    private int HitTestScreen(int screenX, int screenY)
    {
        double dx = screenX - _originX;
        double dy = screenY - _originY;
        double neutral = RingInner * _layoutScale * _scale;

        // Zona neutra central (repouso / cancelar / voltar).
        if (dx * dx + dy * dy < neutral * neutral) return -1;

        return RadialGeometry.HitTest(dx, dy, neutral, _currentSlices.Count);
    }

    // ------------------------------------------------------------------
    // Hover e espera (dwell)
    // ------------------------------------------------------------------

    private void SetHover(int index, bool fromKeyboard = false)
    {
        if (index == _hover) return;

        if (_hover >= 0 && _hover < _segments.Count)
            AnimateSegment(_segments[_hover], on: false);

        _hover = index;

        _dwellTimer.Stop();
        _backDwellTimer.Stop();
        HideAllDwellArcs();

        if (index >= 0 && index < _segments.Count)
        {
            AnimateSegment(_segments[index], on: true);
            RadialSlice slice = _currentSlices[index];
            ShowHub(slice.Glyph);   // o miolo acompanha a fatia escolhida
            bool hasSub = slice.SubSlices is { Count: >= 2 };
            ShowHint(fromKeyboard
                ? KeyboardHint(slice, hasSub)
                : hasSub
                    ? (string.IsNullOrEmpty(slice.Description) ? "Segure para ver as opções" : $"{slice.Description} · segure para ver as opções")
                    : slice.Description);

            // Pelo teclado o submenu abre no Enter: nada de espera nem de traço de tempo.
            if (hasSub && !fromKeyboard)
            {
                _dwellTargetIndex = index;
                StartDwellProgress(_segments[index], DwellDuration);
                _dwellTimer.Start();
            }
        }
        else if (_navHistory.Count > 0)
        {
            // No centro, dentro de um submenu: segurar volta ao menu anterior.
            ShowHub(null);   // seta "voltar"
            ShowHint("Segure no centro para voltar");
            StartBackDwellProgress(DwellDuration);   // o círculo fecha exatamente quando volta
            _backDwellTimer.Start();
        }
        else
        {
            ShowHub(null);   // marca em repouso
            HideHint();
        }
    }

    private void OnDwellTimerTick(object? sender, EventArgs e)
    {
        _dwellTimer.Stop();
        HideAllDwellArcs();

        if (_hover >= 0 && _hover == _dwellTargetIndex && _hover < _currentSlices.Count &&
            _currentSlices[_hover].SubSlices is { Count: >= 2 })
            EnterSubmenu(_currentSlices[_hover], _hover);
    }

    private void OnBackDwellTimerTick(object? sender, EventArgs e)
    {
        _backDwellTimer.Stop();
        HideAllDwellArcs();
        if (_hover == -1 && _navHistory.Count > 0) ReturnToParentMenu();
    }

    private void EnterSubmenu(RadialSlice parentSlice, int parentIndex, bool fromKeyboard = false)
    {
        _navHistory.Push((_currentSlices, parentIndex, parentSlice.Label));
        _currentSlices = RadialSlicePaging.Paginate(parentSlice.SubSlices);

        BuildSegments();
        PlayBlossomTransition();

        // Pelo teclado, o foco vai para a primeira opção; pelo mouse, permanece sob o cursor.
        if (fromKeyboard) SelectByKeyboard(0);
        else RefreshHover();
    }

    private void ReturnToParentMenu(bool fromKeyboard = false)
    {
        if (_navHistory.Count == 0) return;

        var (slices, parentIndex, _) = _navHistory.Pop();
        _currentSlices = slices;

        BuildSegments();
        PlayBlossomTransition();

        // Esc devolve o destaque à fatia que abriu o submenu.
        if (fromKeyboard) SelectByKeyboard(parentIndex);
        else RefreshHover();
    }

    // ------------------------------------------------------------------
    // Miolo central e dica
    // ------------------------------------------------------------------

    /// <summary>
    /// Miolo da roda. Com <paramref name="glyph"/>: ícone da fatia sob o cursor. Sem ele: a marca em anel
    /// (menu principal) ou a seta "voltar" (dentro de um submenu).
    /// A marca sai de cena trocando só a opacidade; a visibilidade continua indicando "menu principal" ou "submenu".
    /// </summary>
    private void ShowHub(string? glyph)
    {
        bool inSubmenu = _navHistory.Count > 0;
        string? shown = glyph ?? (inSubmenu ? "\uE72B" : null);   // \uE72B = seta "voltar"
        var d = TimeSpan.FromMilliseconds(120);

        IdleDot.Visibility = inSubmenu ? Visibility.Collapsed : Visibility.Visible;
        IdleDot.BeginAnimation(OpacityProperty, new DoubleAnimation(shown is null ? 1 : 0, d));

        if (shown is not null) HubGlyph.Text = shown;
        HubGlyph.BeginAnimation(OpacityProperty, new DoubleAnimation(shown is null ? 0 : 1, d));

        // Pequeno "pulo" ao trocar de ícone.
        if (glyph is not null)
        {
            var pulse = new DoubleAnimation(0.78, 1.0, TimeSpan.FromMilliseconds(160))
            {
                EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.4 }
            };
            HubGlyphScale.BeginAnimation(ScaleTransform.ScaleXProperty, pulse);
            HubGlyphScale.BeginAnimation(ScaleTransform.ScaleYProperty, pulse);
        }
    }
    private void ShowHint(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            HideHint();
            return;
        }

        HintText.Text = text;
        HintPill.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(90)));
    }

    private void HideHint() =>
        HintPill.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(70)));

    // ------------------------------------------------------------------
    // Animações da janela
    // ------------------------------------------------------------------

    private void PlayAppearAnimation()
    {
        Root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(90)));

        var spring = new DoubleAnimation(0.78, 1.0, TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.22 }
        };
        RootScale.BeginAnimation(ScaleTransform.ScaleXProperty, spring);
        RootScale.BeginAnimation(ScaleTransform.ScaleYProperty, spring);
    }

    private void PlayBlossomTransition()
    {
        var blossom = new DoubleAnimation(0.84, 1.0, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.18 }
        };
        var fade = new DoubleAnimation(0.3, 1.0, TimeSpan.FromMilliseconds(150));

        var scale = new ScaleTransform(1, 1, Center, Center);
        WheelCanvas.RenderTransform = scale;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, blossom);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, blossom);
        WheelCanvas.BeginAnimation(OpacityProperty, fade);
    }

    internal static double GetMonitorScale(int x, int y)
    {
        IntPtr monitor = MonitorFromPoint(new NativePoint { X = x, Y = y }, MONITOR_DEFAULTTONEAREST);
        return GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out uint dpiX, out _) == 0
            ? dpiX / 96.0
            : 1.0;
    }

    /// <summary>
    /// Centro da roda para um gesto iniciado em (x, y): o próprio ponto, ou o mais próximo que mantém a roda
    /// inteira dentro do monitor. Chamada rápida de Win32; segura em qualquer thread.
    /// </summary>
    internal static (int X, int Y) PlaceOnScreen(int x, int y, double layoutScale = 1.0)
    {
        IntPtr monitor = MonitorFromPoint(new NativePoint { X = x, Y = y }, MONITOR_DEFAULTTONEAREST);
        var info = new MonitorInfo { cbSize = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return (x, y);

        return RadialPlacement.Clamp(x, y, info.rcMonitor.Left, info.rcMonitor.Top,
            info.rcMonitor.Right, info.rcMonitor.Bottom, GetMonitorScale(x, y) * layoutScale);
    }

    /// <summary>Só para testes: mesma conta polar usada pelo cursor real.</summary>
    internal int HitTestForTests(int screenX, int screenY) => HitTestScreen(screenX, screenY);

    /// <summary>Leva o cursor para (x, y). Usado quando a roda foi deslocada para caber na tela.</summary>
    internal static void MoveCursor(int x, int y) => SetCursorPos(x, y);

    /// <summary>Área útil (sem a barra de tarefas) do monitor que contém o ponto, em pixels físicos.</summary>
    internal static (int Left, int Top, int Right, int Bottom) GetWorkArea(int x, int y)
    {
        IntPtr monitor = MonitorFromPoint(new NativePoint { X = x, Y = y }, MONITOR_DEFAULTTONEAREST);
        var info = new MonitorInfo { cbSize = (uint)Marshal.SizeOf<MonitorInfo>() };
        return monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info)
            ? (info.rcWork.Left, info.rcWork.Top, info.rcWork.Right, info.rcWork.Bottom)
            : (0, 0, 1920, 1080);
    }

    /// <summary>Move uma janela WPF para (x, y) em pixels físicos, sem mudar tamanho nem ativação.</summary>
    internal static void MoveWindowPx(Window window, int x, int y)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd != IntPtr.Zero) SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    // ------------------------------------------------------------------
    // P/Invoke
    // ------------------------------------------------------------------

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private static readonly IntPtr HWND_TOPMOST = new(-1);

    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const int MDT_EFFECTIVE_DPI = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public uint cbSize;
        public NativeRect rcMonitor;
        public NativeRect rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint pt, uint dwFlags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hMonitor, int dpiType, out uint dpiX, out uint dpiY);
}
