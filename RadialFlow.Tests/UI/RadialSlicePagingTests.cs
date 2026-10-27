using System.Collections.Generic;
using System.Linq;
using RadialFlow.UI;
using Xunit;

namespace RadialFlow.Tests;

/// <summary>
/// Paginação de listas longas em páginas de no máximo 8: a última fatia vira "Mais…" e abre a
/// página seguinte. Nenhuma opção pode ser descartada e a ordem original deve ser preservada.
/// </summary>
public sealed class RadialSlicePagingTests
{
    private static RadialSlice Leaf(string label) => new(label, "\uE8AB");

    private static List<RadialSlice> Leaves(int count) =>
        Enumerable.Range(1, count).Select(i => Leaf($"F{i}")).ToList();

    /// <summary>Percorre as páginas em profundidade e devolve os rótulos das folhas, na ordem.</summary>
    private static List<string> Flatten(IReadOnlyList<RadialSlice> page)
    {
        var labels = new List<string>();
        foreach (RadialSlice slice in page)
        {
            if (slice.SubSlices is null) labels.Add(slice.Label);
            else labels.AddRange(Flatten(slice.SubSlices));
        }
        return labels;
    }

    [Fact]
    public void Vazio_NaoCriaPagina()
    {
        Assert.Empty(RadialSlicePaging.Paginate(null));
        Assert.Empty(RadialSlicePaging.Paginate([]));
    }

    [Fact]
    public void AteOito_MantemAListaIntacta()
    {
        var slices = Leaves(8);

        var page = RadialSlicePaging.Paginate(slices);

        Assert.Equal(slices, page);
        Assert.All(page, s => Assert.Null(s.SubSlices));
    }

    [Fact]
    public void NoveOpcoes_SeteMaisMaisDuas()
    {
        var page = RadialSlicePaging.Paginate(Leaves(9));

        Assert.Equal(8, page.Count);
        Assert.Equal(new[] { "F1", "F2", "F3", "F4", "F5", "F6", "F7", RadialSlicePaging.MoreLabel },
            page.Select(s => s.Label));
        Assert.Equal("Mais 2 opções", page[7].Description);
        Assert.Equal(new[] { "F8", "F9" }, page[7].SubSlices!.Select(s => s.Label));
    }

    [Fact]
    public void DozeOpcoes_PreservamOrdemENenhumaSome()
    {
        var page = RadialSlicePaging.Paginate(Leaves(12));

        Assert.Equal(8, page.Count);
        Assert.Equal("Mais 5 opções", page[7].Description);
        Assert.Equal(Enumerable.Range(1, 12).Select(i => $"F{i}"), Flatten(page));
    }

    [Fact]
    public void MuitasOpcoes_CriamPaginasEncadeadasDeAte8()
    {
        var page = RadialSlicePaging.Paginate(Leaves(25));

        Assert.Equal(8, page.Count);
        Assert.Equal(Enumerable.Range(1, 25).Select(i => $"F{i}"), Flatten(page));

        int pages = 0;
        for (IReadOnlyList<RadialSlice>? current = page; current is not null; current = current[^1].SubSlices)
        {
            pages++;
            Assert.InRange(current.Count, 2, RadialGeometry.MaxSlices);
            if (current[^1].SubSlices is not null) Assert.Equal(RadialSlicePaging.MoreLabel, current[^1].Label);
        }
        Assert.Equal(4, pages);   // 7 + 7 + 7 + 4
    }

    [Fact]
    public void PaginaPequena_NaoCriaPaginaSoDeMais()
    {
        // pageSize 2: cada página leva 1 opção + "Mais…" até a última, que fica sozinha.
        var page = RadialSlicePaging.Paginate(Leaves(4), pageSize: 2);

        Assert.Equal(2, page.Count);
        Assert.Equal(RadialSlicePaging.MoreLabel, page[1].Label);
        Assert.Equal(Enumerable.Range(1, 4).Select(i => $"F{i}"), Flatten(page));
    }

    [Fact]
    public void PaginaSizeInvalido_NuncaCriaPaginaSoDeMais()
    {
        var page = RadialSlicePaging.Paginate(Leaves(2), pageSize: 1);

        Assert.Equal(2, page.Count);
        Assert.Null(page[1].SubSlices);
    }
}
