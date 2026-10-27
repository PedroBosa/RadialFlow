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

/// <summary>Origem de uma fatia no plano de mesclagem.</summary>
internal enum CustomMergeStatus
{
    /// <summary>Fatia do menu automático do contexto.</summary>
    Automatic,

    /// <summary>Item personalizado que assumiu o lugar de uma fatia automática.</summary>
    Replacement,

    /// <summary>Item personalizado acrescentado ao fim da roda.</summary>
    Extra,
}

internal sealed record CustomMergeEntry(RadialSlice Slice, CustomMenuItem? Item, CustomMergeStatus Status);

/// <summary>Como a roda ficaria com os itens informados, incluindo os que não couberam.</summary>
internal sealed record CustomMergePlan(
    IReadOnlyList<CustomMergeEntry> Entries,
    IReadOnlyList<CustomMenuItem> Dropped,
    IReadOnlyList<CustomMenuItem> Invalid);

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
        return Plan(automatic, items, execute).Entries.Select(e => e.Slice).ToList();
    }

    /// <summary>
    /// Mesma regra de <see cref="Merge"/>, mas devolve também a origem de cada fatia e os
    /// itens que não couberam (o editor usa isso para a prévia e o aviso de limite).
    /// </summary>
    internal static CustomMergePlan Plan(
        IReadOnlyList<RadialSlice> automatic,
        IReadOnlyList<CustomMenuItem> items,
        Action<CustomMenuItem> execute)
    {
        var entries = new List<CustomMergeEntry>(automatic.Count);
        foreach (RadialSlice slice in automatic)
            entries.Add(new(slice, null, CustomMergeStatus.Automatic));

        var dropped = new List<CustomMenuItem>();
        var invalid = new List<CustomMenuItem>();
        var replaced = new HashSet<int>();

        foreach (CustomMenuItem item in items)
        {
            if (item.Kind == CustomMenuItemKind.Submenu &&
                CustomMenuNormalizer.ActiveOptions(item).Count < CustomMenuNormalizer.MinOptions)
            {
                invalid.Add(item);
                AppLog.Debug("Menus", $"Submenu '{item.Label}' ignorado: precisa de {CustomMenuNormalizer.MinOptions} opções ativas.");
                continue;
            }

            if (item.Kind == CustomMenuItemKind.Routine &&
                !CustomRoutineLogic.TryBuildPlan(item, out _, out string? routineReason))
            {
                invalid.Add(item);
                AppLog.Debug("Menus", $"Rotina '{item.Label}' ignorada: {routineReason}");
                continue;
            }

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
                entries[replaceIndex] = new(slice, item, CustomMergeStatus.Replacement);
                replaced.Add(replaceIndex);
            }
            else if (entries.Count < RadialGeometry.MaxSlices)
            {
                entries.Add(new(slice, item, CustomMergeStatus.Extra));
            }
            else
            {
                dropped.Add(item);
                AppLog.Debug("Menus", $"Item '{item.Label}' ignorado: a roda já tem {RadialGeometry.MaxSlices} fatias.");
            }
        }

        return new(entries, dropped, invalid);
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

    private static RadialSlice ToSlice(CustomMenuItem item, Action<CustomMenuItem> execute)
    {
        IReadOnlyList<RadialSlice>? options = null;
        if (item.Kind == CustomMenuItemKind.Submenu)
        {
            options = CustomMenuNormalizer.ActiveOptions(item)
                .Select(option => new RadialSlice(
                    option.Label,
                    GlyphOf(option),
                    () => execute(option),
                    null,
                    string.IsNullOrWhiteSpace(option.Description) ? option.Label : option.Description))
                .ToList();
        }

        return new(
            item.Label,
            GlyphOf(item),
            options is null ? () => execute(item) : null,
            options,
            string.IsNullOrWhiteSpace(item.Description) ? item.Label : item.Description,
            item.Kind == CustomMenuItemKind.Action ? ActionKey(item.Action) : null);
    }

    private static string GlyphOf(CustomMenuItem item) =>
        string.IsNullOrWhiteSpace(item.Glyph) ? "\uE734" : item.Glyph;

    private static string ActionKey(RadialAction action) => action.ToString();
}
