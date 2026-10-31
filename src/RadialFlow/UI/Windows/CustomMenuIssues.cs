// ============================================================================
// RadialFlow - Avisos por item do menu
// ----------------------------------------------------------------------------
// Mesmas regras que o Salvar recusaria, resumidas para a lista de itens: o
// usuário vê o problema no item, antes de tentar salvar. Nada aqui toca disco
// (a confiança do caminho continua sendo revalidada na execução).
// ============================================================================

using System.Linq;
using RadialFlow.Config;
using RadialFlow.Infrastructure;

namespace RadialFlow.UI;

internal static class CustomMenuIssues
{
    /// <summary>Motivo curto e localizado do primeiro problema do item; null quando ele está válido.</summary>
    internal static string? Describe(CustomMenuItem? item)
    {
        if (item is null) return null;

        string label = (item.Label ?? "").Trim();
        if (label.Length == 0) return Loc.Get("Menus.IssueLabel");
        if (label.Length > CustomMenuNormalizer.MaxLabelLength)
            return Loc.Format("Menus.ValidateLabelTooLong", label, CustomMenuNormalizer.MaxLabelLength);

        if (item.Kind == CustomMenuItemKind.Submenu)
        {
            var options = (item.Options ?? []).Where(o => o is not null).ToList();
            if (options.Count > CustomMenuNormalizer.MaxOptions)
                return Loc.Format("Menus.ValidateSubmenuTooMany", label, CustomMenuNormalizer.MaxOptions);

            var active = CustomMenuNormalizer.ActiveOptions(item);
            if (active.Count < CustomMenuNormalizer.MinOptions)
                return Loc.Format("Menus.ValidateSubmenuMinOptions", label, CustomMenuNormalizer.MinOptions);

            foreach (CustomMenuItem option in active)
            {
                string? error = CustomMenuValidation.ValidateOption(option, label);
                if (error is not null) return error;
            }

            return null;
        }

        if ((item.Description ?? "").Length > CustomMenuNormalizer.MaxDescriptionLength)
            return Loc.Format("Menus.ValidateDescriptionTooLong", label, CustomMenuNormalizer.MaxDescriptionLength);

        return CustomMenuValidation.ValidateTarget(item, label);
    }

    /// <summary>Itens habilitados que ainda têm algum problema (os desabilitados não entram na roda).</summary>
    internal static int CountEnabled(CustomMenuProfile? profile) =>
        (profile?.Items ?? []).Count(i => i is not null && i.Enabled && Describe(i) is not null);
}
