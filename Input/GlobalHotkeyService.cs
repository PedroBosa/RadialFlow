// ============================================================================
// RadialFlow - Atalho global (RegisterHotKey)
// ----------------------------------------------------------------------------
// A roda é uma janela WS_EX_NOACTIVATE, então não existe "foco" para onde
// mandar a tecla: o atalho que abre a roda é registrado no sistema com
// RegisterHotKey. A janela que recebe o WM_HOTKEY é própria e invisível
// (message-only), e não a da roda: assim o atalho funciona com a roda fechada
// e sobrevive ao Prewarm/Hide da janela do menu.
//
// Invariantes:
//   * _desired é o que o usuário quer (persistido); _registered, o que o
//     sistema realmente concedeu; _enabled, se o atalho deve valer agora
//     (pausa pelo ícone da bandeja desliga).
//   * Trocar o atalho desregistra o antigo ANTES de tentar o novo. Se o novo
//     falhar (outro programa já usa), o antigo é restaurado — o usuário não
//     fica sem nenhum atalho por causa de uma tentativa malsucedida.
//   * MOD_NOREPEAT evita a repetição enquanto a combinação fica segurada.
// ============================================================================

using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using RadialFlow.Infrastructure;

namespace RadialFlow.Input;

internal enum HotkeyRegistrationError
{
    None,

    /// <summary>O texto não representa uma combinação válida.</summary>
    Invalid,

    /// <summary>Outro programa já registrou a combinação.</summary>
    AlreadyInUse,

    /// <summary>Não foi possível criar a janela receptora ou o Windows recusou o registro.</summary>
    Unavailable
}

internal sealed class GlobalHotkeyService : IDisposable
{
    private const int HotkeyId = 1;
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_NOREPEAT = 0x4000;
    private const int ErrorHotkeyAlreadyRegistered = 1409;

    // HWND_MESSAGE: janela só de mensagens, sem presença na tela, no Alt+Tab ou na barra de tarefas.
    private static readonly IntPtr HwndMessage = new(-3);

    private HwndSource? _source;
    private IntPtr _hwnd = IntPtr.Zero;
    private HotkeyGesture? _desired;
    private bool _registered;
    private bool _enabled = true;
    private bool _disposed;
    private HotkeyRegistrationError _lastError;

    /// <summary>Disparado na thread de UI quando o usuário pressiona a combinação.</summary>
    public event Action? Pressed;

    internal bool IsRegistered => _registered;
    internal bool IsEnabled => _enabled;
    internal string Hotkey => _desired?.ToString() ?? "";
    internal HotkeyRegistrationError LastError => _lastError;

    /// <summary>Só para testes: HWND do receptor e id usado no WM_HOTKEY.</summary>
    internal IntPtr Handle => _hwnd;
    internal static int RegistrationId => HotkeyId;

    /// <summary>
    /// Define a combinação desejada. <paramref name="normalized"/> devolve a forma canônica ("" = sem atalho).
    /// Em falha, o registro anterior é restaurado (quando havia um) e o valor desejado não muda.
    /// </summary>
    public HotkeyRegistrationError TrySet(string? hotkey, out string normalized)
    {
        normalized = HotkeyGesture.Normalize(hotkey);
        if (!string.IsNullOrWhiteSpace(hotkey) && normalized.Length == 0)
            return Remember(HotkeyRegistrationError.Invalid);

        if (_disposed) return Remember(HotkeyRegistrationError.Unavailable);

        HotkeyGesture? desired = null;
        if (normalized.Length > 0)
        {
            if (!HotkeyGesture.TryParse(normalized, out HotkeyGesture parsed))
                return Remember(HotkeyRegistrationError.Invalid);
            desired = parsed;
        }

        if (desired == _desired)
        {
            // Mesmo valor: nada a fazer, exceto tentar registrar de novo se o sistema ainda não concedeu
            // (ex.: conflito resolvido enquanto o app estava aberto).
            if (desired is null || _registered || !_enabled)
                return Remember(HotkeyRegistrationError.None);

            return RegisterOrReport(desired.Value);
        }

        HotkeyGesture? previous = _desired;
        bool previousRegistered = _registered;

        Unregister();

        if (desired is null)
        {
            _desired = null;
            return Remember(HotkeyRegistrationError.None);
        }

        HotkeyRegistrationError error = RegisterOrReport(desired.Value);
        if (error == HotkeyRegistrationError.None)
        {
            _desired = desired;

            // Pausado: valida que a combinação está livre, mas não a segura enquanto a roda está desligada.
            if (!_enabled) Unregister();

            _lastError = HotkeyRegistrationError.None;
            return HotkeyRegistrationError.None;
        }

        _desired = previous;
        if (previousRegistered && previous is not null)
        {
            HotkeyRegistrationError restore = RegisterOrReport(previous.Value);
            if (restore != HotkeyRegistrationError.None)
                AppLog.Warning("Hotkey", $"Não foi possível restaurar o atalho anterior ({FormatForLog(previous.Value)}): {restore}.");
        }

        return Remember(error);
    }

    /// <summary>Liga/desliga o atalho sem perder a combinação desejada (pausa pelo ícone da bandeja).</summary>
    public HotkeyRegistrationError SetEnabled(bool enabled)
    {
        if (_disposed) return Remember(HotkeyRegistrationError.Unavailable);
        if (_enabled == enabled)
            return enabled && _desired is not null && !_registered
                ? RegisterOrReport(_desired.Value)
                : Remember(HotkeyRegistrationError.None);

        _enabled = enabled;

        if (!enabled)
        {
            Unregister();
            return Remember(HotkeyRegistrationError.None);
        }

        if (_desired is null) return Remember(HotkeyRegistrationError.None);
        if (_registered) return Remember(HotkeyRegistrationError.None);

        return RegisterOrReport(_desired.Value);
    }

    private HotkeyRegistrationError RegisterOrReport(HotkeyGesture gesture)
    {
        if (!EnsureWindow())
            return Remember(HotkeyRegistrationError.Unavailable);

        if (RegisterHotKey(_hwnd, HotkeyId, gesture.Win32Modifiers | MOD_NOREPEAT, gesture.VirtualKey))
        {
            _registered = true;
            return Remember(HotkeyRegistrationError.None);
        }

        _registered = false;
        int code = Marshal.GetLastWin32Error();
        HotkeyRegistrationError error = code == ErrorHotkeyAlreadyRegistered
            ? HotkeyRegistrationError.AlreadyInUse
            : HotkeyRegistrationError.Unavailable;

        AppLog.Warning("Hotkey", $"RegisterHotKey falhou para {FormatForLog(gesture)} (erro {code}).");
        return Remember(error);
    }

    private HotkeyRegistrationError Remember(HotkeyRegistrationError error)
    {
        _lastError = error;
        return error;
    }

    private static string FormatForLog(HotkeyGesture gesture) => gesture.ToString();

    private bool EnsureWindow()
    {
        if (_source is not null) return _hwnd != IntPtr.Zero;

        try
        {
            var parameters = new HwndSourceParameters("RadialFlowHotkey")
            {
                WindowStyle = 0,
                Width = 0,
                Height = 0,
                PositionX = 0,
                PositionY = 0,
                ParentWindow = HwndMessage
            };

            _source = new HwndSource(parameters);
            _hwnd = _source.Handle;
            if (_hwnd == IntPtr.Zero)
            {
                DisposeSource();
                return false;
            }

            _source.AddHook(OnHotkeyMessage);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warning("Hotkey", "Não foi possível criar a janela receptora do atalho global.", ex);
            DisposeSource();
            return false;
        }
    }

    private IntPtr OnHotkeyMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_HOTKEY || unchecked((int)wParam.ToInt64()) != HotkeyId) return IntPtr.Zero;

        handled = true;
        try
        {
            Pressed?.Invoke();
        }
        catch (Exception ex)
        {
            AppLog.Error("Hotkey", "Falha ao tratar o atalho global", ex);
        }

        return IntPtr.Zero;
    }

    private void Unregister()
    {
        if (!_registered) return;

        if (_hwnd != IntPtr.Zero) UnregisterHotKey(_hwnd, HotkeyId);
        _registered = false;
    }

    private void DisposeSource()
    {
        if (_source is not null)
        {
            _source.RemoveHook(OnHotkeyMessage);
            _source.Dispose();
            _source = null;
        }

        _hwnd = IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lastError = HotkeyRegistrationError.None;

        Unregister();
        DisposeSource();
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
