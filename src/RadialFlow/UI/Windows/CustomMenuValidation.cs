// ============================================================================
// RadialFlow - Validação de itens de menu personalizado
// ----------------------------------------------------------------------------
// Regras compartilhadas pelo editor da aba Menus e pelo diálogo de opções do
// submenu. Só valida o que o usuário pode corrigir na hora; a revalidação de
// segurança do alvo acontece em CustomActionLogic no momento da execução.
// ============================================================================

using RadialFlow.Config;
using RadialFlow.Context;
using RadialFlow.Infrastructure;
using RadialFlow.Security;
using RadialFlow.Services;

namespace RadialFlow.UI;

internal static class CustomMenuValidation
{
    /// <summary>Valida rótulo, descrição, aninhamento e alvo de uma opção de submenu.</summary>
    internal static string? ValidateOption(CustomMenuItem option, string submenuLabel)
    {
        string label = (option.Label ?? "").Trim();
        if (label.Length == 0)
            return Loc.Format("MenuValidation.OptionMissingLabel", submenuLabel);
        if (label.Length > CustomMenuNormalizer.MaxLabelLength)
            return Loc.Format("MenuValidation.OptionLabelTooLong", label, submenuLabel, CustomMenuNormalizer.MaxLabelLength);
        if ((option.Description ?? "").Length > CustomMenuNormalizer.MaxDescriptionLength)
            return Loc.Format("MenuValidation.OptionDescriptionTooLong", label, CustomMenuNormalizer.MaxDescriptionLength);
        if (option.Kind == CustomMenuItemKind.Submenu)
            return Loc.Format("MenuValidation.NestedSubmenu", submenuLabel);
        if (option.Kind == CustomMenuItemKind.Routine)
            return Loc.Format("MenuValidation.RoutineInside", submenuLabel);

        return ValidateTarget(option, label, isOption: true);
    }

    internal static string? ValidateTarget(CustomMenuItem item, string label, bool isOption = false)
    {
        string noun = Loc.Get(isOption ? "MenuValidation.NounOption" : "MenuValidation.NounItem");

        switch (item.Kind)
        {
            case CustomMenuItemKind.Action:
                if (!System.Enum.IsDefined(item.Action))
                    return Loc.Format("MenuValidation.ActionMissing", noun, label);
                break;

            case CustomMenuItemKind.Launch:
                if (string.IsNullOrWhiteSpace(item.Target))
                    return Loc.Format("MenuValidation.LaunchRequired", noun, label);
                break;

            case CustomMenuItemKind.OpenUrl:
                if (!UnelevatedLauncher.IsSafeUrl(item.Target))
                    return Loc.Format("MenuValidation.UrlInvalid", noun, label);
                break;

            case CustomMenuItemKind.OpenPath:
                if (string.IsNullOrWhiteSpace(item.Target))
                    return Loc.Format("MenuValidation.PathRequired", noun, label);
                if (!TrustedPath.TryGetSafeFullPath(item.Target, out _))
                    return Loc.Format("MenuValidation.PathInvalid", noun, label);
                break;

            case CustomMenuItemKind.Hotkey:
                if (!CustomActionLogic.TryParseHotkey(item.Target, out _, out string? reason))
                    return Loc.Format("MenuValidation.HotkeyInvalid", noun, label, reason);
                break;

            case CustomMenuItemKind.Text:
                if (string.IsNullOrEmpty(item.Text))
                    return Loc.Format("MenuValidation.TextRequired", noun, label);
                if (item.Text.Length > CustomMenuNormalizer.MaxTextLength)
                    return Loc.Format("MenuValidation.TextTooLong", noun, label, CustomMenuNormalizer.MaxTextLength);
                break;

            case CustomMenuItemKind.Routine:
                if (!CustomRoutineLogic.TryBuildPlan(item, out _, out string? routineReason))
                    return Loc.Format("MenuValidation.RoutineInvalid", noun, label, routineReason);
                break;
        }

        return null;
    }
}
