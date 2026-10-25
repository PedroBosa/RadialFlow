// ============================================================================
// RadialFlow - Execução dos itens de menu personalizados
// ----------------------------------------------------------------------------
// Só é chamado quando o usuário escolhe uma fatia personalizada. Valida ANTES
// de qualquer efeito e roda em background, como as ações nativas.
//
// Launch/OpenUrl/OpenPath saem pelo UnelevatedLauncher (processo-filho sem
// elevação). Hotkey/Text restauram o foco na janela de origem via TextTarget e
// nunca enviam teclas para outro aplicativo.
// ============================================================================

using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using RadialFlow.Config;
using RadialFlow.Infrastructure;
using RadialFlow.Services;

namespace RadialFlow.Context;

public static partial class ActionRouter
{
    /// <summary>Ponto de entrada das fatias personalizadas. Nunca lança.</summary>
    public static void RunCustom(CustomMenuItem item, ContextInfo ctx)
    {
        if (item is null || !item.Enabled) return;

        if (item.Kind == CustomMenuItemKind.Action)
        {
            Run(item.Action, ctx);
            return;
        }

        if (!CustomActionLogic.TryValidate(item, out CustomActionPlan plan, out string? reason))
        {
            AppLog.Warning("Menus", $"'{item.Label}' recusado: {reason}");
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await DispatchCustomAsync(plan, ctx).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppLog.Error("Menus", $"Erro em '{item.Label}'", ex);
            }
        });
    }

    private static Task DispatchCustomAsync(CustomActionPlan plan, ContextInfo ctx)
    {
        switch (plan.Kind)
        {
            case CustomMenuItemKind.Launch:
            case CustomMenuItemKind.OpenUrl:
            case CustomMenuItemKind.OpenPath:
                if (!UnelevatedLauncher.Start(plan.Target))
                    AppLog.Warning("Menus", $"Não foi possível abrir '{plan.Target}'.");
                break;

            case CustomMenuItemKind.Hotkey:
                if (!TextTarget.Prepare(ctx, strict: false).Ready)
                {
                    AppLog.Debug("Menus", "Atalho cancelado: a janela de origem não está em foco.");
                    break;
                }
                SimulateKeys(plan.Keys);
                AppLog.Debug("Menus", $"Atalho '{plan.Target}'.");
                break;

            case CustomMenuItemKind.Text:
                PasteCustomText(plan.Text, ctx);
                break;

            default:
                AppLog.Warning("Menus", $"Tipo de item desconhecido: {plan.Kind}");
                break;
        }

        return Task.CompletedTask;
    }

    /// <summary>Cola o texto e tenta devolver o conteúdo anterior do clipboard.</summary>
    private static void PasteCustomText(string text, ContextInfo ctx)
    {
        if (!TextTarget.Prepare(ctx, strict: false).Ready)
        {
            AppLog.Debug("Menus", "Texto cancelado: a janela de origem não está em foco.");
            return;
        }

        string? previousText = _ui.Invoke(() =>
        {
            try { return Clipboard.ContainsText() ? Clipboard.GetText() : null; }
            catch { return null; }
        });

        bool copied = _ui.Invoke(() =>
        {
            try
            {
                Clipboard.SetText(text);
                return true;
            }
            catch (Exception ex)
            {
                AppLog.Warning("Menus", $"Área de transferência indisponível: {ex.Message}");
                return false;
            }
        });

        if (!copied) return;

        SimulateKeys(VK_CONTROL, VK_V);
        Thread.Sleep(150);   // dá tempo de o app de destino consumir o Ctrl+V

        _ui.Invoke(() =>
        {
            try
            {
                if (previousText is not null) Clipboard.SetText(previousText);
                else Clipboard.Clear();
            }
            catch { /* melhor esforço: o texto já foi colado */ }
        });

        AppLog.Debug("Menus", "Texto colado.");
    }
}
