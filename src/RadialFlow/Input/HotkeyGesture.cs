// ============================================================================
// RadialFlow - Combinação de teclas do atalho global
// ----------------------------------------------------------------------------
// Texto canônico ("Ctrl+Alt+Space") usado no settings.json e na interface.
// A leitura aceita aliases ("control", "alt", "space", "F5"), mas a gravação
// sempre devolve a mesma forma — assim uma configuração editada à mão não cria
// variantes do mesmo atalho.
//
// Regras (deliberadamente estritas):
//   * pelo menos um modificador (Ctrl, Alt, Shift ou Win);
//   * exatamente uma tecla final;
//   * sem modificador repetido;
//   * teclas suportadas: A–Z, 0–9, F1–F24 e as teclas nomeadas da tabela.
// A exigência de modificador evita que um atalho global engula uma tecla solta
// e conflite com a navegação da própria roda (que usa números e setas).
//
// Os valores de HotkeyModifiers são os mesmos do Win32 (MOD_ALT etc.), então
// o valor pode ir direto para RegisterHotKey.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;

namespace RadialFlow.Input;

[Flags]
internal enum HotkeyModifiers : uint
{
    None = 0,
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Win = 0x0008
}

internal readonly record struct HotkeyGesture(HotkeyModifiers Modifiers, uint VirtualKey)
{
    internal const int MaxTextLength = 64;

    private const uint VK_BACK = 0x08;
    private const uint VK_TAB = 0x09;
    private const uint VK_RETURN = 0x0D;
    private const uint VK_ESCAPE = 0x1B;
    private const uint VK_SPACE = 0x20;
    private const uint VK_PRIOR = 0x21;
    private const uint VK_NEXT = 0x22;
    private const uint VK_END = 0x23;
    private const uint VK_HOME = 0x24;
    private const uint VK_LEFT = 0x25;
    private const uint VK_UP = 0x26;
    private const uint VK_RIGHT = 0x27;
    private const uint VK_DOWN = 0x28;
    private const uint VK_INSERT = 0x2D;
    private const uint VK_DELETE = 0x2E;

    private static readonly Dictionary<string, uint> KeyAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Enter"] = VK_RETURN, ["Return"] = VK_RETURN,
        ["Esc"] = VK_ESCAPE, ["Escape"] = VK_ESCAPE,
        ["Tab"] = VK_TAB,
        ["Space"] = VK_SPACE, ["Spacebar"] = VK_SPACE,
        ["Backspace"] = VK_BACK, ["Back"] = VK_BACK,
        ["Delete"] = VK_DELETE, ["Del"] = VK_DELETE,
        ["Insert"] = VK_INSERT, ["Ins"] = VK_INSERT,
        ["Home"] = VK_HOME, ["End"] = VK_END,
        ["PageUp"] = VK_PRIOR, ["PgUp"] = VK_PRIOR,
        ["PageDown"] = VK_NEXT, ["PgDn"] = VK_NEXT,
        ["Left"] = VK_LEFT, ["ArrowLeft"] = VK_LEFT,
        ["Up"] = VK_UP, ["ArrowUp"] = VK_UP,
        ["Right"] = VK_RIGHT, ["ArrowRight"] = VK_RIGHT,
        ["Down"] = VK_DOWN, ["ArrowDown"] = VK_DOWN
    };

    private static readonly Dictionary<uint, string> KeyNames = new()
    {
        [VK_RETURN] = "Enter",
        [VK_ESCAPE] = "Esc",
        [VK_TAB] = "Tab",
        [VK_SPACE] = "Space",
        [VK_BACK] = "Backspace",
        [VK_DELETE] = "Delete",
        [VK_INSERT] = "Insert",
        [VK_HOME] = "Home",
        [VK_END] = "End",
        [VK_PRIOR] = "PageUp",
        [VK_NEXT] = "PageDown",
        [VK_LEFT] = "Left",
        [VK_UP] = "Up",
        [VK_RIGHT] = "Right",
        [VK_DOWN] = "Down"
    };

    /// <summary>Valor para o parâmetro fsModifiers de RegisterHotKey.</summary>
    internal uint Win32Modifiers => (uint)Modifiers;

    public override string ToString() => Format(this);

    /// <summary>Forma canônica do texto ou "" se ele não representar um atalho válido.</summary>
    internal static string Normalize(string? text) =>
        TryParse(text, out HotkeyGesture gesture) ? Format(gesture) : "";

    internal static bool TryParse(string? text, out HotkeyGesture gesture)
    {
        gesture = default;

        string value = (text ?? "").Trim();
        if (value.Length == 0 || value.Length > MaxTextLength) return false;

        string[] parts = value.Split('+');
        if (parts.Length is < 2 or > 5) return false;   // 4 modificadores + a tecla

        HotkeyModifiers modifiers = HotkeyModifiers.None;
        uint? virtualKey = null;

        foreach (string rawPart in parts)
        {
            string part = rawPart.Trim();
            if (part.Length == 0) return false;

            if (TryParseModifier(part, out HotkeyModifiers modifier))
            {
                if ((modifiers & modifier) != 0) return false;
                modifiers |= modifier;
            }
            else
            {
                if (virtualKey is not null) return false;
                if (!TryParseKey(part, out uint parsed)) return false;
                virtualKey = parsed;
            }
        }

        if (virtualKey is null || modifiers == HotkeyModifiers.None) return false;

        gesture = new HotkeyGesture(modifiers, virtualKey.Value);
        return true;
    }

    /// <summary>Monta a combinação a partir de uma tecla e dos modificadores capturados na interface.</summary>
    internal static bool TryCreate(HotkeyModifiers modifiers, uint virtualKey, out HotkeyGesture gesture)
    {
        gesture = default;

        const HotkeyModifiers all = HotkeyModifiers.Alt | HotkeyModifiers.Control |
                                     HotkeyModifiers.Shift | HotkeyModifiers.Win;
        if (modifiers == HotkeyModifiers.None || (modifiers & ~all) != 0) return false;
        if (!TryDescribeKey(virtualKey, out _)) return false;

        gesture = new HotkeyGesture(modifiers, virtualKey);
        return true;
    }

    internal static string Format(HotkeyGesture gesture)
    {
        var parts = new List<string>(4);
        if ((gesture.Modifiers & HotkeyModifiers.Control) != 0) parts.Add("Ctrl");
        if ((gesture.Modifiers & HotkeyModifiers.Alt) != 0) parts.Add("Alt");
        if ((gesture.Modifiers & HotkeyModifiers.Shift) != 0) parts.Add("Shift");
        if ((gesture.Modifiers & HotkeyModifiers.Win) != 0) parts.Add("Win");

        parts.Add(TryDescribeKey(gesture.VirtualKey, out string key)
            ? key
            : throw new ArgumentException("Tecla não suportada no atalho global.", nameof(gesture)));

        return string.Join("+", parts);
    }

    private static bool TryParseModifier(string token, out HotkeyModifiers modifier)
    {
        modifier = token.ToLowerInvariant() switch
        {
            "ctrl" or "control" => HotkeyModifiers.Control,
            "alt" => HotkeyModifiers.Alt,
            "shift" => HotkeyModifiers.Shift,
            "win" or "windows" or "meta" or "super" => HotkeyModifiers.Win,
            _ => HotkeyModifiers.None
        };
        return modifier != HotkeyModifiers.None;
    }

    private static bool TryParseKey(string token, out uint virtualKey)
    {
        virtualKey = 0;

        if (token.Length == 1)
        {
            char c = token[0];
            if (c is >= 'a' and <= 'z') { virtualKey = (uint)('A' + (c - 'a')); return true; }
            if (c is >= 'A' and <= 'Z') { virtualKey = (uint)('A' + (c - 'A')); return true; }
            if (c is >= '0' and <= '9') { virtualKey = (uint)('0' + (c - '0')); return true; }
            return false;
        }

        if (token.Length is 2 or 3 && token[0] is 'f' or 'F' &&
            int.TryParse(token.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out int number) &&
            number is >= 1 and <= 24)
        {
            virtualKey = (uint)(0x70 + number - 1);
            return true;
        }

        return KeyAliases.TryGetValue(token, out virtualKey);
    }

    private static bool TryDescribeKey(uint virtualKey, out string name)
    {
        if (virtualKey is >= 0x41 and <= 0x5A)   // A–Z
        {
            name = ((char)virtualKey).ToString();
            return true;
        }

        if (virtualKey is >= 0x30 and <= 0x39)   // 0–9
        {
            name = ((char)virtualKey).ToString();
            return true;
        }

        if (virtualKey is >= 0x70 and <= 0x87)   // F1–F24
        {
            name = "F" + (virtualKey - 0x70 + 1).ToString(CultureInfo.InvariantCulture);
            return true;
        }

        return KeyNames.TryGetValue(virtualKey, out name!);
    }
}
