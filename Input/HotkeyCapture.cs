// ============================================================================
// RadialFlow - Captura da combinação nas Configurações
// ----------------------------------------------------------------------------
// Traduz a tecla que chega ao WPF (PreviewKeyDown) para a mesma representação
// usada pelo RegisterHotKey. Fica separado da janela para poder ser testado
// sem sintetizar o estado real do teclado: a janela só resolve Key.System
// (Alt+tecla) e entrega a tecla e os modificadores.
// ============================================================================

using System.Windows.Input;

namespace RadialFlow.Input;

internal enum HotkeyCaptureResult
{
    /// <summary>Combinação válida; <c>gesture</c> está preenchido.</summary>
    Captured,

    /// <summary>Só um modificador foi pressionado: continue esperando a tecla final.</summary>
    ModifierOnly,

    /// <summary>Tecla sem nenhum modificador; o atalho global exige pelo menos um.</summary>
    MissingModifier,

    /// <summary>Tecla fora do conjunto suportado.</summary>
    Unsupported,

    /// <summary>Esc sem modificadores: cancela a captura.</summary>
    Cancelled
}

internal static class HotkeyCapture
{
    internal static HotkeyCaptureResult TryCapture(Key key, ModifierKeys modifiers, out HotkeyGesture gesture)
    {
        gesture = default;

        if (key == Key.Escape && modifiers == ModifierKeys.None) return HotkeyCaptureResult.Cancelled;
        if (IsModifierKey(key)) return HotkeyCaptureResult.ModifierOnly;
        if (modifiers == ModifierKeys.None) return HotkeyCaptureResult.MissingModifier;

        HotkeyModifiers flags = HotkeyModifiers.None;
        if ((modifiers & ModifierKeys.Control) != 0) flags |= HotkeyModifiers.Control;
        if ((modifiers & ModifierKeys.Alt) != 0) flags |= HotkeyModifiers.Alt;
        if ((modifiers & ModifierKeys.Shift) != 0) flags |= HotkeyModifiers.Shift;
        if ((modifiers & ModifierKeys.Windows) != 0) flags |= HotkeyModifiers.Win;

        uint virtualKey = unchecked((uint)KeyInterop.VirtualKeyFromKey(key));
        return HotkeyGesture.TryCreate(flags, virtualKey, out gesture)
            ? HotkeyCaptureResult.Captured
            : HotkeyCaptureResult.Unsupported;
    }

    internal static bool IsModifierKey(Key key) => key is
        Key.LeftCtrl or Key.RightCtrl or
        Key.LeftShift or Key.RightShift or
        Key.LeftAlt or Key.RightAlt or
        Key.LWin or Key.RWin;

    /// <summary>"Ctrl+Alt" — usado só como prévia ao vivo enquanto o usuário ainda segura os modificadores.</summary>
    internal static string DescribeModifiers(ModifierKeys modifiers)
    {
        var parts = new System.Collections.Generic.List<string>(4);
        if ((modifiers & ModifierKeys.Control) != 0) parts.Add("Ctrl");
        if ((modifiers & ModifierKeys.Alt) != 0) parts.Add("Alt");
        if ((modifiers & ModifierKeys.Shift) != 0) parts.Add("Shift");
        if ((modifiers & ModifierKeys.Windows) != 0) parts.Add("Win");
        return string.Join("+", parts);
    }
}
