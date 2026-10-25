// ============================================================================
// RadialFlow - Resolução dos menus personalizados
// ----------------------------------------------------------------------------
// Funções PURAS: casam perfis com o contexto e mesclam os itens às fatias
// automáticas. Nada aqui executa ação; o delegate de execução entra por
// parâmetro (SliceFactory liga ao ActionRouter).
//
// Regras de mesclagem:
//  * item de ação substitui a fatia automática da MESMA ação, na mesma posição;
//  * fatias com submenu (Converter, Apps favoritos) nunca são substituídas;
//  * o restante é acrescentado ao fim, até o limite de 8 fatias da roda;
//  * sem itens, a lista automática é devolvida intacta.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using RadialFlow.Config;
using RadialFlow.Infrastructure;
using RadialFlow.UI;

namespace RadialFlow.Context;

internal static class CustomMenuResolver
{
    /// <summary>Itens habilitados dos perfis que casam com o contexto, em ordem determinística.</summary>
    internal static IReadOnlyList<CustomMenuItem> Match(
        ContextInfo ctx, IReadOnlyList<CustomMenuProfile>? profiles)
    {
        if (profiles is null || profiles.Count == 0)
            return Array.Empty<CustomMenuItem>();

        IEnumerable<CustomMenuProfile> matched = profiles
            .Where(p => p is not null && p.Enabled && Matches(p, ctx))
            .OrderByDescending(p => p.Priority)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Id, StringComparer.Ordinal);

        var items = new List<CustomMenuItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (CustomMenuProfile profile in matched)
        {
            foreach (CustomMenuItem item in profile.Items ?? [])
            {
                if (item is null || !item.Enabled) continue;
                if (!seen.Add(item.Id)) continue;
                items.Add(item);
            }
        }

        return items;
    }

    /// <summary>Mescla os itens às fatias automáticas; <paramref name="execute"/> roda quando a fatia é escolhida.</summary>
    internal static IReadOnlyList<RadialSlice> Merge(
        IReadOnlyList<RadialSlice> automatic,
        IReadOnlyList<CustomMenuItem> items,
        Action<CustomMenuItem> execute)
    {
        if (items.Count == 0) return automatic;

        var merged = new List<RadialSlice>(automatic);
        var replaced = new HashSet<int>();

        foreach (CustomMenuItem item in items)
        {
            int replaceIndex = -1;

            if (item.Kind == CustomMenuItemKind.Action)
            {
                for (int i = 0; i < automatic.Count; i++)
                {
                    if (replaced.Contains(i)) continue;
                    if (automatic[i].ActionKey == ActionKey(item.Action) && automatic[i].SubSlices is null)
                    {
                        replaceIndex = i;
                        break;
                    }
                }
            }

            RadialSlice slice = ToSlice(item, execute);

            if (replaceIndex >= 0)
            {
                merged[replaceIndex] = slice;
                replaced.Add(replaceIndex);
            }
            else if (merged.Count < RadialGeometry.MaxSlices)
            {
                merged.Add(slice);
            }
            else
            {
                AppLog.Debug("Menus", $"Item '{item.Label}' ignorado: a roda já tem {RadialGeometry.MaxSlices} fatias.");
            }
        }

        return merged;
    }

    private static bool Matches(CustomMenuProfile profile, ContextInfo ctx)
    {
        if (profile.Contexts is { Count: > 0 } && !profile.Contexts.Contains(ctx.Kind))
            return false;

        if (profile.ProcessName is { Length: > 0 } &&
            !string.Equals(
                CustomMenuNormalizer.NormalizeProcessName(profile.ProcessName),
                ctx.ProcessName,
                StringComparison.OrdinalIgnoreCase))
            return false;

        if (profile.TitleContains is { Length: > 0 } &&
            !ctx.WindowTitle.Contains(profile.TitleContains, StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }

    private static RadialSlice ToSlice(CustomMenuItem item, Action<CustomMenuItem> execute) =>
        new(
            item.Label,
            string.IsNullOrWhiteSpace(item.Glyph) ? "\uE734" : item.Glyph,
            () => execute(item),
            null,
            string.IsNullOrWhiteSpace(item.Description) ? item.Label : item.Description,
            item.Kind == CustomMenuItemKind.Action ? ActionKey(item.Action) : null);

    private static string ActionKey(RadialAction action) => action.ToString();
}
