// ============================================================================
// RadialFlow - Paginação das fatias da roda
// ----------------------------------------------------------------------------
// Separado de RadialModels.cs porque o gerador de ícones (tools/IconGenerator)
// compila a geometria sem o restante do app; aqui entram os textos traduzidos.
// ============================================================================

using System.Collections.Generic;
using RadialFlow.Infrastructure;

namespace RadialFlow.UI;

/// <summary>
/// Divide uma lista longa em páginas que cabem na roda. Quando as opções não cabem, a última fatia vira
/// "Mais…" e abre a página seguinte; a ordem original é preservada e nenhuma opção é descartada.
/// </summary>
public static class RadialSlicePaging
{
    public static string MoreLabel => Loc.Get("Common.More");
    public const string MoreGlyph = "\uE712";   // ícone "More" da Segoe Fluent Icons

    internal static List<RadialSlice> Paginate(
        IReadOnlyList<RadialSlice>? slices, int pageSize = RadialGeometry.MaxSlices)
    {
        var page = new List<RadialSlice>();
        if (slices is null || slices.Count == 0) return page;

        if (pageSize < 2) pageSize = 2;   // uma página com só "Mais…" nunca abriria (submenu exige 2 opções)

        if (slices.Count <= pageSize)
        {
            page.AddRange(slices);
            return page;
        }

        int firstPageCount = pageSize - 1;
        for (int i = 0; i < firstPageCount; i++) page.Add(slices[i]);

        var rest = new List<RadialSlice>(slices.Count - firstPageCount);
        for (int i = firstPageCount; i < slices.Count; i++) rest.Add(slices[i]);

        page.Add(new RadialSlice(
            Label: MoreLabel,
            Glyph: MoreGlyph,
            SubSlices: Paginate(rest, pageSize),
            Description: rest.Count == 1 ? Loc.Get("Common.MoreOneOption") : Loc.Format("Common.MoreOptions", rest.Count)));

        return page;
    }
}
