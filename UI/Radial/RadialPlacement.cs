// ============================================================================
// RadialFlow - Posição da roda na tela
// ----------------------------------------------------------------------------
// Perto de uma borda (barra de tarefas, canto) a roda era cortada pela tela.
// O centro é deslocado o mínimo necessário para o conteúdo visível caber no
// monitor; o RadialController leva o cursor para o novo centro (senão ele já
// nasceria pairando sobre uma fatia) e captura o fundo de vidro nesse centro.
//
// Pura (sem Win32): recebe os limites do monitor em pixels físicos.
// ============================================================================

using System;

namespace RadialFlow.UI;

internal static class RadialPlacement
{
    /// <summary>Meia largura visível da roda em DIPs: anel (170) + salto da fatia (6%) + traço de espera + folga.</summary>
    internal const double SideMargin = 196;

    /// <summary>Meia altura acima do centro, em DIPs.</summary>
    internal const double TopMargin = 196;

    /// <summary>Altura abaixo do centro, em DIPs: inclui a etiqueta de dica, que fica sob o anel.</summary>
    internal const double BottomMargin = 222;

    /// <summary>
    /// Centro da roda mais próximo de (originX, originY) que mantém tudo dentro do monitor.
    /// Se o monitor for menor que a roda em algum eixo, centraliza nesse eixo.
    /// </summary>
    /// <param name="left">Limites do monitor em pixels físicos.</param>
    /// <param name="scale">Escala de DPI do monitor (1,0 = 96 dpi).</param>
    public static (int X, int Y) Clamp(int originX, int originY, int left, int top, int right, int bottom, double scale)
    {
        scale = scale > 0 ? scale : 1.0;

        int x = ClampAxis(originX, left + SideMargin * scale, right - SideMargin * scale);
        int y = ClampAxis(originY, top + TopMargin * scale, bottom - BottomMargin * scale);
        return (x, y);
    }

    private static int ClampAxis(int value, double min, double max) =>
        min > max
            ? (int)Math.Round((min + max) / 2)   // não cabe: o melhor possível é centralizar
            : (int)Math.Round(Math.Clamp(value, min, max));

    /// <summary>
    /// Canto superior esquerdo mais próximo de (left, top) que mantém uma janela de width x height dentro da
    /// área informada (pixels físicos). Se a janela for maior que a área em algum eixo, alinha à borda inicial.
    /// </summary>
    public static (int X, int Y) ClampRect(int left, int top, int width, int height,
        int areaLeft, int areaTop, int areaRight, int areaBottom)
    {
        int x = Math.Clamp(left, areaLeft, Math.Max(areaLeft, areaRight - width));
        int y = Math.Clamp(top, areaTop, Math.Max(areaTop, areaBottom - height));
        return (x, y);
    }
}
