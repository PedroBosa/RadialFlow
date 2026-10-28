using System.IO;
using RadialFlow.Context;
using RadialFlow.Tests.TestSupport;
using Xunit;

namespace RadialFlow.Tests;

public sealed class ExplorerItemResolverTests : System.IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private string Folder => _temp.Root;

    [Fact]
    public void NomeExato_TemPreferenciaSobreAsExtensoes()
    {
        _temp.CreateFile("Relatorio");
        _temp.CreateFile("Relatorio.docx");

        Assert.Equal(_temp.Combine("Relatorio"), ExplorerItemResolver.FindInFolder(Folder, "Relatorio"));
    }

    [Fact]
    public void ExtensaoOculta_ComUmSoCandidato_Resolve()
    {
        _temp.CreateFile("Relatorio.docx");

        Assert.Equal(_temp.Combine("Relatorio.docx"), ExplorerItemResolver.FindInFolder(Folder, "Relatorio"));
    }

    [Fact]
    public void ExtensaoOculta_ComDoisCandidatos_NaoEscolheNenhum()
    {
        _temp.CreateFile("Relatorio.docx");
        _temp.CreateFile("Relatorio.pdf");

        Assert.Null(ExplorerItemResolver.FindInFolder(Folder, "Relatorio"));
    }

    [Fact]
    public void PastaComPonto_ResolveQuandoUnica()
    {
        _temp.CreateDirectory("Projetos.old");

        Assert.Equal(_temp.Combine("Projetos.old"), ExplorerItemResolver.FindInFolder(Folder, "Projetos"));
    }

    [Fact]
    public void SemCandidato_NaoResolve() =>
        Assert.Null(ExplorerItemResolver.FindInFolder(Folder, "Ausente"));
}
