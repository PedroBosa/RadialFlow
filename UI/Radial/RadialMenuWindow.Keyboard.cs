// ============================================================================
// RadialFlow - Menu radial: navegação por teclado
// ----------------------------------------------------------------------------
// A roda é WS_EX_NOACTIVATE (nunca rouba o foco), então não recebe teclas pelo
// foco normal. Enquanto ela está VISÍVEL, registramos atalhos globais com
// RegisterHotKey no próprio HWND da roda (validado: WM_HOTKEY chega mesmo sem
// foco). Ao esconder a janela os atalhos são cancelados; fora da roda o teclado
// continua indo para o aplicativo de baixo. Não há hook de teclado e nenhuma
// tecla é lida ou armazenada.
//
//   1–8       escolhe a fatia pela posição (submenu abre; ação executa)
//   ← ↑ ↓ →   circula pelas fatias
//   Enter     abre o submenu da fatia escolhida ou executa a ação
//   Esc       volta ao menu anterior; no menu principal, fecha a roda
//
// A seleção por teclado usa o mesmo destaque do mouse e o mouse retoma o
// controle assim que se move mais de alguns pixels. O soltar do botão direito
// respeita a fatia escolhida pelo teclado.
// ============================================================================

using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using RadialFlow.Infrastructure;

namespace RadialFlow.UI;

public partial class RadialMenuWindow
{
    internal enum RadialKeyboardCommand
    {
        Previous,
        Next,
        Activate,
        Back,
        Select1, Select2, Select3, Select4, Select5, Select6, Select7, Select8
    }

    /// <summary>Atalhos registrados enquanto a roda está visível. O índice + 1 é o id do RegisterHotKey.</summary>
    internal static readonly (uint VirtualKey, RadialKeyboardCommand Command)[] Hotkeys =
    {
        (0x26, RadialKeyboardCommand.Previous),   // ↑
        (0x25, RadialKeyboardCommand.Previous),   // ←
        (0x27, RadialKeyboardCommand.Next),       // →
        (0x28, RadialKeyboardCommand.Next),       // ↓
        (0x0D, RadialKeyboardCommand.Activate),   // Enter
        (0x1B, RadialKeyboardCommand.Back),       // Esc
        (0x31, RadialKeyboardCommand.Select1),    // 1
        (0x32, RadialKeyboardCommand.Select2),    // 2
        (0x33, RadialKeyboardCommand.Select3),    // 3
        (0x34, RadialKeyboardCommand.Select4),    // 4
        (0x35, RadialKeyboardCommand.Select5),    // 5
        (0x36, RadialKeyboardCommand.Select6),    // 6
        (0x37, RadialKeyboardCommand.Select7),    // 7
        (0x38, RadialKeyboardCommand.Select8)     // 8
    };

    private bool _keyboardNavigationEnabled;
    private bool _keyboardMode;
    private int _keyboardIndex = -1;
    private int _keyboardAnchorX, _keyboardAnchorY;
    private HwndSource? _hwndSource;
    private readonly bool[] _hotkeysRegistered = new bool[Hotkeys.Length];

    /// <summary>Fatia executada pelo teclado (Enter ou número). O controller escuta para rodar a ação.</summary>
    public event Action<RadialSlice>? SliceActivated;

    internal bool IsKeyboardSelectionActive => _keyboardMode;
    internal int KeyboardSelection => _keyboardMode ? _keyboardIndex : -1;

    /// <summary>Liga o registro dos atalhos. Testes chamam <see cref="HandleKeyboardCommand"/> direto.</summary>
    internal void EnableKeyboardNavigation()
    {
        _keyboardNavigationEnabled = true;
        if (IsVisible) RegisterHotkeys();
    }

    internal static bool TryMapHotkey(int id, out RadialKeyboardCommand command)
    {
        if (id >= 1 && id <= Hotkeys.Length)
        {
            command = Hotkeys[id - 1].Command;
            return true;
        }

        command = default;
        return false;
    }

    // ------------------------------------------------------------------
    // Comandos (chamados pelo WM_HOTKEY e pelos testes)
    // ------------------------------------------------------------------

    internal void HandleKeyboardCommand(RadialKeyboardCommand command)
    {
        if (!IsVisible || _currentSlices.Count == 0) return;

        switch (command)
        {
            case RadialKeyboardCommand.Previous:
                MoveSelection(-1);
                break;

            case RadialKeyboardCommand.Next:
                MoveSelection(+1);
                break;

            case RadialKeyboardCommand.Activate:
                ActivateSelection();
                break;

            case RadialKeyboardCommand.Back:
                GoBack();
                break;

            default:
                int index = (int)command - (int)RadialKeyboardCommand.Select1;
                if (index >= 0 && index < _currentSlices.Count)
                {
                    if (HasSubmenu(index)) EnterSubmenu(_currentSlices[index], index, fromKeyboard: true);
                    else ExecuteSelection(index);
                }
                break;
        }
    }

    private void MoveSelection(int delta)
    {
        int count = _currentSlices.Count;
        int current = _keyboardMode ? _keyboardIndex : -1;

        int next = current < 0
            ? (delta > 0 ? 0 : count - 1)
            : ((current + delta) % count + count) % count;

        SelectByKeyboard(next);
    }

    private void ActivateSelection()
    {
        int index = _keyboardIndex >= 0 ? _keyboardIndex : _hover >= 0 ? _hover : 0;
        if (index >= _currentSlices.Count) return;

        if (HasSubmenu(index)) EnterSubmenu(_currentSlices[index], index, fromKeyboard: true);
        else ExecuteSelection(index);
    }

    private void GoBack()
    {
        if (_navHistory.Count > 0) ReturnToParentMenu(fromKeyboard: true);
        else Cancel();
    }

    private bool HasSubmenu(int index) => _currentSlices[index].SubSlices is { Count: >= 2 };

    // ------------------------------------------------------------------
    // Seleção e dica
    // ------------------------------------------------------------------

    private void SelectByKeyboard(int index)
    {
        if (index < 0 || index >= _currentSlices.Count) return;

        if (!_keyboardMode)
        {
            _keyboardAnchorX = _cursorX;
            _keyboardAnchorY = _cursorY;
        }

        _keyboardMode = true;
        _keyboardIndex = index;

        if (index == _hover && _hover >= 0)
        {
            // O mouse já destacava esta fatia: troca a dica e cancela a espera do submenu.
            _dwellTimer.Stop();
            _backDwellTimer.Stop();
            HideAllDwellArcs();
            ShowHint(KeyboardHint(_currentSlices[index], HasSubmenu(index)));
            return;
        }

        SetHover(index, fromKeyboard: true);
    }

    private static string KeyboardHint(RadialSlice slice, bool hasSubmenu)
    {
        string action = hasSubmenu ? Loc.Get("Wheel.EnterOpensOptions") : Loc.Get("Wheel.EnterRuns");
        return string.IsNullOrEmpty(slice.Description) ? action : $"{slice.Description} · {action}";
    }

    private void ExecuteSelection(int index)
    {
        if (index < 0 || index >= _currentSlices.Count) return;

        RadialSlice slice = _currentSlices[index];
        HideWheel();   // a ação pode abrir diálogo: a roda sai de cena antes
        SliceActivated?.Invoke(slice);
    }

    private void ResetKeyboardState()
    {
        _keyboardMode = false;
        _keyboardIndex = -1;
    }

    // ------------------------------------------------------------------
    // Registro dos atalhos (somente enquanto a roda está visível)
    // ------------------------------------------------------------------

    internal void AttachHotkeyHandler()
    {
        if (_hwndSource is not null) return;

        _hwndSource = HwndSource.FromHwnd(_hwnd);
        _hwndSource?.AddHook(OnHotkeyMessage);
    }

    private void DetachHotkeyHandler()
    {
        _hwndSource?.RemoveHook(OnHotkeyMessage);
        _hwndSource = null;
    }

    private IntPtr OnHotkeyMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_HOTKEY || !TryMapHotkey(wParam.ToInt32(), out RadialKeyboardCommand command))
            return IntPtr.Zero;

        handled = true;
        try { HandleKeyboardCommand(command); }
        catch (Exception ex) { AppLog.Error("Wheel", "Falha ao tratar atalho da roda", ex); }

        return IntPtr.Zero;
    }

    private void RegisterHotkeys()
    {
        if (!_keyboardNavigationEnabled || _hwnd == IntPtr.Zero) return;

        for (int i = 0; i < Hotkeys.Length; i++)
        {
            if (_hotkeysRegistered[i]) continue;

            _hotkeysRegistered[i] = RegisterHotKey(_hwnd, i + 1, MOD_NOREPEAT, Hotkeys[i].VirtualKey);
            if (!_hotkeysRegistered[i])
            {
                // Outro programa pode já ter registrado a combinação: a roda continua utilizável com o mouse
                // (e com as teclas que ainda puderem ser registradas).
                AppLog.Debug("Wheel",
                    $"Atalho indisponível (VK 0x{Hotkeys[i].VirtualKey:X2}, erro {Marshal.GetLastWin32Error()}).");
            }
        }
    }

    internal void UnregisterHotkeys()
    {
        if (_hwnd == IntPtr.Zero) return;

        for (int i = 0; i < _hotkeysRegistered.Length; i++)
        {
            if (!_hotkeysRegistered[i]) continue;

            UnregisterHotKey(_hwnd, i + 1);
            _hotkeysRegistered[i] = false;
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        UnregisterHotkeys();
        DetachHotkeyHandler();
        base.OnClosing(e);
    }

    // ------------------------------------------------------------------
    // P/Invoke
    // ------------------------------------------------------------------

    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
