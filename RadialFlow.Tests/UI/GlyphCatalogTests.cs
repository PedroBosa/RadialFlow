using System;
using System.Linq;
using RadialFlow.UI;
using Xunit;

namespace RadialFlow.Tests;

/// <summary>Conjunto curado de ícones dos menus: local, sem duplicatas e com rótulos utilizáveis.</summary>
public sealed class GlyphCatalogTests
{
    [Fact]
    public void Icones_SaoLocaisSemDuplicatas_ERotulosSaoUnicos()
    {
        var glyphs = CustomMenuChoices.Glyphs;

        Assert.True(glyphs.Length >= 40, "o conjunto curado precisa cobrir mais do que as ações básicas");
        Assert.Equal(glyphs.Length, glyphs.Select(g => g.Glyph).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(glyphs.Length, glyphs.Select(g => g.Label).Distinct(StringComparer.OrdinalIgnoreCase).Count());

        Assert.All(glyphs, g =>
        {
            Assert.Equal(1, g.Glyph.Length);
            Assert.False(char.IsControl(g.Glyph[0]));
            Assert.False(string.IsNullOrWhiteSpace(g.Label));
            Assert.True(g.Label.Length <= 24);
        });
    }
}
