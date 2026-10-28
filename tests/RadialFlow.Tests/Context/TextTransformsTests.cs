using RadialFlow.Config;
using RadialFlow.Context;
using Xunit;

namespace RadialFlow.Tests;

public sealed class TextTransformsTests
{
    [Fact]
    public void Upper_UsaCaixaAltaInvariante()
    {
        Assert.Equal("OLÁ MUNDO", TextTransforms.Apply("olá mundo", CustomTextTransform.Upper));
    }

    [Fact]
    public void Lower_UsaCaixaBaixaInvariante()
    {
        Assert.Equal("olá mundo", TextTransforms.Apply("OLÁ MUNDO", CustomTextTransform.Lower));
    }

    [Fact]
    public void TitleCase_CapitalizaCadaPalavra()
    {
        Assert.Equal("Olá Mundo De Teste", TextTransforms.Apply("olá MUNDO de teste", CustomTextTransform.TitleCase));
    }

    [Fact]
    public void TitleCase_NaoCapitalizaDepoisDeApostrofo()
    {
        Assert.Equal("Don't Stop", TextTransforms.Apply("don't stop", CustomTextTransform.TitleCase));
    }

    [Fact]
    public void SentenceCase_CapitalizaInicioEFrases()
    {
        Assert.Equal(
            "Olá mundo. Tudo bem! E agora? Sim",
            TextTransforms.Apply("olá mundo. tudo bem! e agora? sim", CustomTextTransform.SentenceCase));
    }

    [Fact]
    public void SentenceCase_CapitalizaDepoisDeQuebraDeLinha()
    {
        Assert.Equal(
            "Primeira linha\nSegunda linha",
            TextTransforms.Apply("primeira linha\nSEGUNDA LINHA", CustomTextTransform.SentenceCase));
    }

    [Fact]
    public void Trim_RemoveEspacosDasPontas()
    {
        Assert.Equal("a b", TextTransforms.Apply("  \t a b \r\n ", CustomTextTransform.Trim));
    }

    [Fact]
    public void RemoveLineBreaks_JuntaLinhasNaoVaziasComUmEspaco()
    {
        Assert.Equal(
            "a b c",
            TextTransforms.Apply("  a  \r\n\r\n b\n\tc  ", CustomTextTransform.RemoveLineBreaks));
    }

    [Fact]
    public void RemoveLineBreaks_TextoVazioContinuaVazio()
    {
        Assert.Equal("", TextTransforms.Apply(" \r\n\r\n ", CustomTextTransform.RemoveLineBreaks));
    }

    [Fact]
    public void TransformacaoDesconhecida_DevolveTextoOriginal()
    {
        Assert.Equal("texto", TextTransforms.Apply("texto", (CustomTextTransform)999));
    }
}
