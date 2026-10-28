using System;
using System.Globalization;
using System.Linq;
using RadialFlow.Infrastructure;
using RadialFlow.UI.Markup;
using Xunit;

namespace RadialFlow.Tests;

public sealed class LocTests : IDisposable
{
    public LocTests()
    {
        Loc.SetLanguage(null);
        Loc.ApplyToCurrentThread();
    }

    public void Dispose()
    {
        Loc.SetLanguage(null);
        Loc.ApplyToCurrentThread();
    }

    // ------------------------------------------------------------------
    // Resolução do idioma gravado
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(null, "pt-BR")]
    [InlineData("", "pt-BR")]
    [InlineData("   ", "pt-BR")]
    [InlineData("pt-BR", "pt-BR")]
    [InlineData("pt-br", "pt-BR")]
    [InlineData("pt", "pt-BR")]
    [InlineData("pt-PT", "pt-BR")]
    [InlineData("en-US", "en-US")]
    [InlineData("EN-us", "en-US")]
    [InlineData("en", "en-US")]
    [InlineData("en-GB", "en-US")]
    [InlineData("es-ES", "es-ES")]
    [InlineData("es", "es-ES")]
    [InlineData("es-MX", "es-ES")]
    [InlineData("fr-FR", "pt-BR")]
    [InlineData("  en-US  ", "en-US")]
    public void Resolve(string? stored, string expected) =>
        Assert.Equal(expected, Loc.Resolve(stored));

    [Fact]
    public void SemEscolha_UsaPortugues()
    {
        Assert.Equal("pt-BR", Loc.Culture.Name);
        Assert.Equal("Cancelar", Loc.Get("Common.Cancel"));
    }

    // ------------------------------------------------------------------
    // Textos
    // ------------------------------------------------------------------

    [Fact]
    public void Ingles_Espanhol_TraduzemAsMesmasChaves()
    {
        Loc.SetLanguage("en-US");
        Assert.Equal("Cancel", Loc.Get("Common.Cancel"));
        Assert.Equal("Save", Loc.Get("Common.Save"));
        Assert.Equal("Yes", Loc.Get("Common.Yes"));

        Loc.SetLanguage("es-ES");
        Assert.Equal("Cancelar", Loc.Get("Common.Cancel"));
        Assert.Equal("Guardar", Loc.Get("Common.Save"));
        Assert.Equal("Sí", Loc.Get("Common.Yes"));
    }

    [Fact]
    public void ChaveAusente_DevolveAPropriaChave() =>
        Assert.Equal("Nao.Existe", Loc.Get("Nao.Existe"));

    [Fact]
    public void ChaveVazia_NaoQuebra() =>
        Assert.Equal("", Loc.Get(""));

    [Fact]
    public void Format_UsaOIdiomaAtual()
    {
        Assert.Equal("Mais 3 opções", Loc.Format("Common.MoreOptions", 3));

        Loc.SetLanguage("en-US");
        Assert.Equal("More 3 options", Loc.Format("Common.MoreOptions", 3));

        Loc.SetLanguage("es-ES");
        Assert.Equal("Más 3 opciones", Loc.Format("Common.MoreOptions", 3));
    }

    [Fact]
    public void LocExtension_ResolveNaConstrucao()
    {
        Loc.SetLanguage("en-US");

        Assert.Equal("Save", new LocExtension("Common.Save").ProvideValue(null!));
    }

    // ------------------------------------------------------------------
    // Catálogo
    // ------------------------------------------------------------------

    [Fact]
    public void TodosOsIdiomas_TemExatamenteAsMesmasChaves()
    {
        string[] reference = Loc.Keys(null).ToArray();
        Assert.NotEmpty(reference);

        foreach (LanguageOption language in Loc.Languages)
            Assert.Equal(reference, Loc.Keys(language.Key).ToArray());
    }

    [Fact]
    public void AplicarNaThread_AlinhaCulturaDeInterface()
    {
        Loc.SetLanguage("en-US");

        Loc.ApplyToCurrentThread();

        Assert.Equal("en-US", CultureInfo.CurrentUICulture.Name);
        Assert.Equal("en-US", CultureInfo.DefaultThreadCurrentUICulture?.Name);
    }
}
