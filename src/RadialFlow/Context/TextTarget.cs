// ============================================================================
// RadialFlow - Alvo de texto: foca o campo CERTO antes de enviar teclas
// ----------------------------------------------------------------------------
// Problema: o gesto engole o RMB Down, então o campo sob o cursor nunca recebe
// foco. Sem este passo, Ctrl+V / Ctrl+A+Del iam para o controle que JÁ tinha
// foco, que podia ser outro campo (ou o documento inteiro).
//
// Solução: localizar o elemento na ORIGEM do gesto via UI Automation e chamar
// SetFocus (não altera a seleção de texto, ao contrário de um clique).
// Se a janela de origem não ficar em primeiro plano, NADA é enviado, para que
// nenhuma tecla vá parar em outro app.
//
// Chamar de thread de pool (MTA), nunca da thread de UI.
// ============================================================================

using System;
using System.Threading;
using System.Windows.Automation;

namespace RadialFlow.Context;

internal static class TextTarget
{
    internal readonly record struct Result(bool Ready, bool IsDocument);

    /// <param name="strict">
    /// true  = só prossegue se o elemento foi focado com certeza (ações destrutivas).
    /// false = se o UIA falhar mas a janela correta estiver em foco, prossegue
    ///         (comportamento anterior, porém protegido contra janela errada).
    /// </param>
    public static Result Prepare(ContextInfo ctx, bool strict)
    {
        WindowFocus.ForceForeground(ctx.RootWindow);
        Thread.Sleep(40);

        if (!WindowFocus.IsForegroundRoot(ctx.RootWindow))
            return default;   // janela de origem não está em foco: aborta

        bool focused = false;
        bool isDocument = false;

        try
        {
            var element = FindFocusable(AutomationElement.FromPoint(new System.Windows.Point(ctx.OriginX, ctx.OriginY)));
            if (element is not null)
            {
                isDocument = element.Current.ControlType == ControlType.Document;
                element.SetFocus();
                Thread.Sleep(40);
                focused = WindowFocus.IsForegroundRoot(ctx.RootWindow);
            }
        }
        catch
        {
            // ElementNotAvailable, InvalidOperation (não focável), COMException...
        }

        return new Result(focused || !strict, isDocument);
    }

    /// <summary>Sobe até 5 níveis procurando o primeiro elemento que aceita foco de teclado.</summary>
    private static AutomationElement? FindFocusable(AutomationElement? element)
    {
        var walker = TreeWalker.ControlViewWalker;
        for (int i = 0; i < 5 && element is not null; i++)
        {
            if (element.Current.IsKeyboardFocusable) return element;
            if (element.Current.ControlType == ControlType.Window) return null;
            element = walker.GetParent(element);
        }
        return null;
    }
}
