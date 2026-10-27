// ============================================================================
// RadialFlow - Validação de itens de menu personalizado
// ----------------------------------------------------------------------------
// Regras compartilhadas pelo editor da aba Menus e pelo diálogo de opções do
// submenu. Só valida o que o usuário pode corrigir na hora; a revalidação de
// segurança do alvo acontece em CustomActionLogic no momento da execução.
// ============================================================================

using RadialFlow.Config;
using RadialFlow.Context;
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
            return $"Há uma opção sem rótulo no submenu “{submenuLabel}”.";
        if (label.Length > CustomMenuNormalizer.MaxLabelLength)
            return $"A opção “{label}” do submenu “{submenuLabel}” passou de {CustomMenuNormalizer.MaxLabelLength} caracteres.";
        if ((option.Description ?? "").Length > CustomMenuNormalizer.MaxDescriptionLength)
            return $"A descrição da opção “{label}” passou de {CustomMenuNormalizer.MaxDescriptionLength} caracteres.";
        if (option.Kind == CustomMenuItemKind.Submenu)
            return $"O submenu “{submenuLabel}” não pode ter outro submenu dentro.";
        if (option.Kind == CustomMenuItemKind.Routine)
            return $"O submenu “{submenuLabel}” não pode ter uma rotina: rotinas ficam no primeiro anel.";

        return ValidateTarget(option, label, "opção");
    }

    internal static string? ValidateTarget(CustomMenuItem item, string label, string noun)
    {
        string article = noun == "opção" ? "da" : "do";

        switch (item.Kind)
        {
            case CustomMenuItemKind.Action:
                if (!System.Enum.IsDefined(item.Action))
                    return $"A ação {article} {noun} “{label}” não existe mais.";
                break;

            case CustomMenuItemKind.Launch:
                if (string.IsNullOrWhiteSpace(item.Target))
                    return $"Informe o executável {article} {noun} “{label}”.";
                break;

            case CustomMenuItemKind.OpenUrl:
                if (!UnelevatedLauncher.IsSafeUrl(item.Target))
                    return $"A URL {article} {noun} “{label}” precisa começar com http:// ou https://.";
                break;

            case CustomMenuItemKind.OpenPath:
                if (string.IsNullOrWhiteSpace(item.Target))
                    return $"Informe o arquivo ou a pasta {article} {noun} “{label}”.";
                if (!TrustedPath.TryGetSafeFullPath(item.Target, out _))
                    return $"O caminho {article} {noun} “{label}” é inválido.";
                break;

            case CustomMenuItemKind.Hotkey:
                if (!CustomActionLogic.TryParseHotkey(item.Target, out _, out string? reason))
                    return $"Atalho {article} {noun} “{label}”: {reason}";
                break;

            case CustomMenuItemKind.Text:
                if (string.IsNullOrEmpty(item.Text))
                    return $"Informe o texto a colar {article} {noun} “{label}”.";
                if (item.Text.Length > CustomMenuNormalizer.MaxTextLength)
                    return $"O texto {article} {noun} “{label}” passou de {CustomMenuNormalizer.MaxTextLength} caracteres.";
                break;

            case CustomMenuItemKind.Routine:
                if (!CustomRoutineLogic.TryBuildPlan(item, out _, out string? routineReason))
                    return $"A rotina {article} {noun} “{label}” está inválida. {routineReason}";
                break;
        }

        return null;
    }
}
