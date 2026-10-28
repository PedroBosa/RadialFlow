// ============================================================================
// RadialFlow - Atalhos próprios dos menus personalizados
// ----------------------------------------------------------------------------
// Decide QUAIS perfis ganham um RegisterHotKey: habilitados, com atalho válido,
// com pelo menos um item ativo e sem repetir o atalho da roda principal nem o
// de outro menu (dois registros iguais fariam o segundo falhar).
//
// Função pura: o controller só registra o que sair daqui; o editor valida as
// mesmas regras antes de salvar.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using RadialFlow.Input;

namespace RadialFlow.Config;

internal static class CustomMenuHotkeys
{
    internal static IReadOnlyList<(CustomMenuProfile Profile, string Hotkey)> Resolve(
        IReadOnlyList<CustomMenuProfile>? profiles, string? wheelHotkey)
    {
        var result = new List<(CustomMenuProfile, string)>();
        if (profiles is null || profiles.Count == 0) return result;

        string wheel = HotkeyGesture.Normalize(wheelHotkey);
        var used = new HashSet<string>(StringComparer.Ordinal);

        foreach (CustomMenuProfile profile in profiles)
        {
            if (profile is null || !profile.Enabled || !HasActiveItem(profile)) continue;

            string hotkey = HotkeyGesture.Normalize(profile.Hotkey);
            if (hotkey.Length == 0) continue;
            if (wheel.Length > 0 && string.Equals(hotkey, wheel, StringComparison.Ordinal)) continue;
            if (!used.Add(hotkey)) continue;

            result.Add((profile, hotkey));
        }

        return result;
    }

    internal static bool HasActiveItem(CustomMenuProfile profile) =>
        (profile.Items ?? new List<CustomMenuItem>()).Any(i => i is not null && i.Enabled);
}
