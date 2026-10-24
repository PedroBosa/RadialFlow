using System;
using System.IO;
using RadialFlow.Services;
using RadialFlow.Tests.TestSupport;
using Xunit;

namespace RadialFlow.Tests;

public sealed class FileNamingTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    // ------------------------------------------------------------------
    // TryGetUniquePath / GetUniquePath: sem colisões
    // ------------------------------------------------------------------

    [Fact]
    public void TryGetUniquePath_CaminhoLivre_DevolveOProprioCaminho()
    {
        string path = _temp.Combine("relatorio.pdf");

        Assert.True(FileNaming.TryGetUniquePath(path, out string unique));
        Assert.Equal(path, unique);
    }

    [Fact]
    public void TryGetUniquePath_ArquivoExistente_NumeraAntesDaExtensao()
    {
        _temp.CreateFile("foto.jpg");

        Assert.True(FileNaming.TryGetUniquePath(_temp.Combine("foto.jpg"), out string unique));
        Assert.Equal(_temp.Combine("foto (1).jpg"), unique);
    }

    [Fact]
    public void TryGetUniquePath_VariosOcupados_UsaOPrimeiroNumeroLivre()
    {
        _temp.CreateFile("a.txt");
        _temp.CreateFile("a (1).txt");
        _temp.CreateFile("a (2).txt");

        Assert.Equal(_temp.Combine("a (3).txt"), FileNaming.GetUniquePath(_temp.Combine("a.txt")));
    }

    [Fact]
    public void TryGetUniquePath_ConsideraPastasEArquivosAoMesmoTempo()
    {
        _temp.CreateFile("x.txt");
        _temp.CreateDirectory("x (1).txt");   // uma PASTA ocupando o nome numerado

        Assert.Equal(_temp.Combine("x (2).txt"), FileNaming.GetUniquePath(_temp.Combine("x.txt")));
    }

    [Fact]
    public void TryGetUniquePath_PastaExistente_NaoTrataPontoComoExtensao()
    {
        _temp.CreateDirectory("meu.projeto");

        Assert.Equal(_temp.Combine("meu.projeto (1)"), FileNaming.GetUniquePath(_temp.Combine("meu.projeto")));
    }

    [Fact]
    public void TryGetUniquePath_PastaComSeparadorFinal_EhNormalizada()
    {
        _temp.CreateDirectory("Fotos");

        Assert.True(FileNaming.TryGetUniquePath(_temp.Combine("Fotos") + "\\", out string unique));
        Assert.Equal(_temp.Combine("Fotos (1)"), unique);
    }

    [Fact]
    public void TryGetUniquePath_ArquivoOcultoSemNome_NumeraONomeInteiro()
    {
        _temp.CreateFile(".gitignore");

        Assert.Equal(_temp.Combine(".gitignore (1)"), FileNaming.GetUniquePath(_temp.Combine(".gitignore")));
    }

    [Fact]
    public void TryGetUniquePath_NaoCriaNadaNoDisco()
    {
        _temp.CreateFile("doc.txt");

        string unique = FileNaming.GetUniquePath(_temp.Combine("doc.txt"));

        Assert.False(File.Exists(unique));
        Assert.False(Directory.Exists(unique));
    }

    [Fact]
    public void TryGetUniquePath_CanonicalizaBarrasNormais()
    {
        string withSlashes = _temp.Combine("sub", "arquivo.txt").Replace('\\', '/');

        Assert.True(FileNaming.TryGetUniquePath(withSlashes, out string unique));
        Assert.Equal(_temp.Combine("sub", "arquivo.txt"), unique);
    }

    [Theory]
    [InlineData("arquivo.txt")]
    [InlineData(@"pasta\arquivo.txt")]
    [InlineData(@"C:\Temp\..\Windows\arquivo.txt")]
    [InlineData(@"C:\Temp\NUL.txt")]
    [InlineData(@"C:\Temp\arquivo.txt:ads")]
    [InlineData("")]
    [InlineData(null)]
    public void TryGetUniquePath_RejeitaCaminhosInseguros(string? path)
    {
        Assert.False(FileNaming.TryGetUniquePath(path, out string unique));
        Assert.Equal("", unique);
    }

    [Fact]
    public void TryGetUniquePath_RaizDoVolume_NaoTemComoNumerar()
    {
        string root = Path.GetPathRoot(Environment.SystemDirectory)!;   // "C:\"
        Assert.False(FileNaming.TryGetUniquePath(root, out _));
    }

    [Fact]
    public void GetUniquePath_CaminhoInseguro_LancaArgumentException()
    {
        Assert.Throws<ArgumentException>(() => FileNaming.GetUniquePath(@"..\..\Windows\System32\x.dll"));
    }

    [Fact]
    public void FormatNumberedName_UsaOPadraoDoExplorer()
    {
        Assert.Equal("nome (3).ext", FileNaming.FormatNumberedName("nome", ".ext", 3));
        Assert.Equal("pasta (12)", FileNaming.FormatNumberedName("pasta", "", 12));
    }

    // ------------------------------------------------------------------
    // TryNormalizeFileName: sanitização de nomes digitados
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("relatório final.docx", "relatório final.docx")]
    [InlineData("  com espaços.txt  ", "com espaços.txt")]
    [InlineData(".gitignore", ".gitignore")]
    [InlineData("foto (1).jpg", "foto (1).jpg")]
    [InlineData("sem-extensao", "sem-extensao")]
    [InlineData("CONSOLE.log", "CONSOLE.log")]
    [InlineData("日本語ファイル.txt", "日本語ファイル.txt")]
    public void TryNormalizeFileName_AceitaNomesValidos(string input, string expected)
    {
        Assert.True(FileNaming.TryNormalizeFileName(input, out string name, out string? error));
        Assert.Equal(expected, name);
        Assert.Null(error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData(@"..\..\Windows\System32\evil.dll")]
    [InlineData("../segredo.txt")]
    [InlineData(@"pasta\arquivo.txt")]
    [InlineData("pasta/arquivo.txt")]
    [InlineData("C:arquivo.txt")]
    [InlineData("arquivo.txt:ads")]
    [InlineData("a*b.txt")]
    [InlineData("a?b.txt")]
    [InlineData("a\"b.txt")]
    [InlineData("a<b.txt")]
    [InlineData("a>b.txt")]
    [InlineData("a|b.txt")]
    [InlineData("termina.")]
    [InlineData("CON")]
    [InlineData("nul.txt")]
    [InlineData("COM1.log")]
    [InlineData("LPT9")]
    public void TryNormalizeFileName_RejeitaNomesInvalidos(string? input)
    {
        Assert.False(FileNaming.TryNormalizeFileName(input, out string name, out string? error));
        Assert.Equal("", name);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Theory]
    [InlineData(0x00)]
    [InlineData(0x07)]
    [InlineData(0x0A)]
    [InlineData(0x1F)]
    public void TryNormalizeFileName_RejeitaCaracteresDeControle(int code)
    {
        Assert.False(FileNaming.TryNormalizeFileName($"a{(char)code}b.txt", out _, out string? error));
        Assert.Contains($"\\u{code:X4}", error);
    }

    [Fact]
    public void TryNormalizeFileName_MensagemIndicaOCaractereProibido()
    {
        Assert.False(FileNaming.TryNormalizeFileName("a|b", out _, out string? error));
        Assert.Contains("'|'", error);
    }

    [Fact]
    public void TryNormalizeFileName_RespeitaOLimiteDoNtfs()
    {
        string max = new('a', FileNaming.MaxFileNameLength);
        string tooLong = new('a', FileNaming.MaxFileNameLength + 1);

        Assert.True(FileNaming.TryNormalizeFileName(max, out string name, out _));
        Assert.Equal(max, name);
        Assert.False(FileNaming.TryNormalizeFileName(tooLong, out _, out _));
    }
}
