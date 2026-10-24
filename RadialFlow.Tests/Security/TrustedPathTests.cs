using System;
using System.IO;
using RadialFlow.Security;
using RadialFlow.Tests.TestSupport;
using Xunit;

namespace RadialFlow.Tests;

public sealed class TrustedPathTests
{
    private static readonly string System32 = Environment.SystemDirectory;
    private static readonly string Cmd = Path.Combine(System32, "cmd.exe");

    // ------------------------------------------------------------------
    // TryGetSafeFullPath: caminhos válidos
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(@"C:\Windows\System32\cmd.exe", @"C:\Windows\System32\cmd.exe")]
    [InlineData(@"c:\Users\Public\file.txt", @"c:\Users\Public\file.txt")]
    [InlineData(@"C:/Users/Public/file.txt", @"C:\Users\Public\file.txt")]
    [InlineData(@"C:\Users\\Public\file.txt", @"C:\Users\Public\file.txt")]
    [InlineData(@"C:\", @"C:\")]
    [InlineData(@"C:\Pasta com espaços\relatório (1).docx", @"C:\Pasta com espaços\relatório (1).docx")]
    [InlineData(@"C:\Users\Public\.gitignore", @"C:\Users\Public\.gitignore")]
    [InlineData(@"\\servidor\compartilhamento\pasta\arquivo.txt", @"\\servidor\compartilhamento\pasta\arquivo.txt")]
    public void TryGetSafeFullPath_AceitaCaminhosAbsolutosECanonicaliza(string input, string expected)
    {
        Assert.True(TrustedPath.TryGetSafeFullPath(input, out string full));
        Assert.Equal(expected, full);
    }

    // ------------------------------------------------------------------
    // TryGetSafeFullPath: travessia de diretório
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(@"C:\Windows\..\Temp\x.exe")]
    [InlineData(@"C:\Windows\System32\..\..\Users\x")]
    [InlineData(@"C:\Pasta\.\arquivo.txt")]
    [InlineData(@"C:\Pasta\..")]
    [InlineData(@"C:/Pasta/../Outra/arquivo.txt")]
    [InlineData(@"\\servidor\compartilhamento\..\outro\x")]
    [InlineData(@"..\Windows\System32\cmd.exe")]
    [InlineData(@"..")]
    public void TryGetSafeFullPath_RejeitaTravessia(string input)
    {
        Assert.False(TrustedPath.TryGetSafeFullPath(input, out string full));
        Assert.Equal("", full);
    }

    [Theory]
    [InlineData("cmd.exe")]
    [InlineData(@"Windows\System32\cmd.exe")]
    [InlineData(@"C:Windows\cmd.exe")]        // relativo ao diretório atual da unidade C:
    [InlineData(@"\Windows\cmd.exe")]          // relativo à unidade atual
    [InlineData("/Windows/cmd.exe")]
    [InlineData("C:")]
    [InlineData(@"~\arquivo.txt")]
    public void TryGetSafeFullPath_RejeitaCaminhosRelativos(string input)
    {
        Assert.False(TrustedPath.TryGetSafeFullPath(input, out _));
    }

    [Theory]
    [InlineData(@"\\?\C:\Windows\System32\cmd.exe")]
    [InlineData(@"\\.\PhysicalDrive0")]
    [InlineData(@"\\.\pipe\radialflow")]
    [InlineData("//?/C:/Windows/System32/cmd.exe")]
    public void TryGetSafeFullPath_RejeitaNamespacesDeDispositivo(string input)
    {
        Assert.False(TrustedPath.TryGetSafeFullPath(input, out _));
    }

    [Theory]
    [InlineData('"')]
    [InlineData('<')]
    [InlineData('>')]
    [InlineData('|')]
    [InlineData('?')]
    [InlineData('*')]
    public void TryGetSafeFullPath_RejeitaCaracteresIlegais(char illegal)
    {
        Assert.False(TrustedPath.TryGetSafeFullPath($@"C:\Pasta\arq{illegal}uivo.txt", out _));
    }

    [Theory]
    [InlineData(0x00)]
    [InlineData(0x09)]
    [InlineData(0x0A)]
    [InlineData(0x0D)]
    [InlineData(0x1F)]
    public void TryGetSafeFullPath_RejeitaCaracteresDeControle(int code)
    {
        Assert.False(TrustedPath.TryGetSafeFullPath($@"C:\Pasta\a{(char)code}b.txt", out _));
    }

    [Theory]
    [InlineData(@"C:\Users\Public\arquivo.txt:oculto")]
    [InlineData(@"C:\Users\Public\arquivo.txt::$DATA")]
    [InlineData(@"C:\a:b\c.txt")]
    [InlineData(@"\\servidor\compartilhamento\arquivo:stream")]
    public void TryGetSafeFullPath_RejeitaStreamsAlternativos(string input)
    {
        Assert.False(TrustedPath.TryGetSafeFullPath(input, out _));
    }

    [Theory]
    [InlineData(@"C:\Temp\CON")]
    [InlineData(@"C:\Temp\con.txt")]
    [InlineData(@"C:\Temp\NUL\arquivo.txt")]
    [InlineData(@"C:\Temp\COM1")]
    [InlineData(@"C:\Temp\LPT9.log")]
    [InlineData(@"C:\Temp\AUX")]
    [InlineData(@"C:\Temp\PRN.doc")]
    [InlineData(@"C:\Temp\CONIN$")]
    [InlineData("C:\\Temp\\COM\u00B9")]
    public void TryGetSafeFullPath_RejeitaNomesReservados(string input)
    {
        Assert.False(TrustedPath.TryGetSafeFullPath(input, out _));
    }

    [Theory]
    [InlineData(@"C:\Temp\pasta.\arquivo.txt")]
    [InlineData(@"C:\Temp\arquivo.txt.")]
    [InlineData(@"C:\Temp\arquivo.txt ")]
    [InlineData(@"C:\Temp\pasta \arquivo.txt")]
    public void TryGetSafeFullPath_RejeitaSegmentoTerminadoEmPontoOuEspaco(string input)
    {
        // O Windows descartaria o ponto/espaço em silêncio, apontando para OUTRO item.
        Assert.False(TrustedPath.TryGetSafeFullPath(input, out _));
    }

    [Theory]
    [InlineData(@"\\servidor")]
    [InlineData(@"\\servidor\")]
    [InlineData(@"\\\compartilhamento\x")]
    public void TryGetSafeFullPath_RejeitaUncIncompleto(string input)
    {
        Assert.False(TrustedPath.TryGetSafeFullPath(input, out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryGetSafeFullPath_RejeitaVazio(string? input)
    {
        Assert.False(TrustedPath.TryGetSafeFullPath(input, out string full));
        Assert.Equal("", full);
    }

    [Fact]
    public void TryGetSafeFullPath_RejeitaCaminhoLongoDemais()
    {
        string huge = @"C:\" + new string('a', 32_768);
        Assert.False(TrustedPath.TryGetSafeFullPath(huge, out _));
    }

    // ------------------------------------------------------------------
    // IsReservedDeviceName
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("CON")]
    [InlineData("con")]
    [InlineData("nul.txt")]
    [InlineData("NUL .txt")]
    [InlineData("COM0")]
    [InlineData("COM9")]
    [InlineData("LPT1.log")]
    [InlineData("CONIN$")]
    [InlineData("CONOUT$")]
    [InlineData("COM\u00B2")]
    [InlineData("LPT\u00B3.txt")]
    public void IsReservedDeviceName_DetectaDispositivos(string name)
    {
        Assert.True(TrustedPath.IsReservedDeviceName(name));
    }

    [Theory]
    [InlineData("CONSOLE")]
    [InlineData("COM10")]
    [InlineData("LPTX")]
    [InlineData("aux1")]
    [InlineData("arquivo.txt")]
    [InlineData("connection.log")]
    [InlineData("")]
    [InlineData(null)]
    public void IsReservedDeviceName_IgnoraNomesComuns(string? name)
    {
        Assert.False(TrustedPath.IsReservedDeviceName(name));
    }

    // ------------------------------------------------------------------
    // IsTrusted: permissões (dono + ACL do arquivo, da pasta e dos ancestrais)
    // ------------------------------------------------------------------

    [Fact]
    public void IsTrusted_AceitaExecutavelDoSystem32()
    {
        Assert.True(TrustedPath.IsTrusted(Cmd));
    }

    [Fact]
    public void IsTrusted_CanonicalizaBarrasNormais()
    {
        Assert.True(TrustedPath.IsTrusted(Cmd.Replace('\\', '/')));
    }

    [Fact]
    public void IsTrusted_RejeitaTravessiaMesmoQueAponteParaArquivoConfiavel()
    {
        string traversal = Path.Combine(System32, "..", "System32", "cmd.exe");
        Assert.True(File.Exists(traversal));   // o arquivo existe de fato...
        Assert.False(TrustedPath.IsTrusted(traversal));   // ...mas a forma do caminho é recusada
    }

    [Fact]
    public void IsTrusted_RejeitaStreamAlternativoDeArquivoConfiavel()
    {
        Assert.False(TrustedPath.IsTrusted(Cmd + ":payload"));
    }

    [Fact]
    public void IsTrusted_RejeitaArquivoInexistente()
    {
        Assert.False(TrustedPath.IsTrusted(Path.Combine(System32, $"radialflow-{Guid.NewGuid():N}.exe")));
    }

    [Fact]
    public void IsTrusted_RejeitaPasta()
    {
        Assert.False(TrustedPath.IsTrusted(System32));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("cmd.exe")]
    public void IsTrusted_RejeitaEntradaInvalida(string? input)
    {
        Assert.False(TrustedPath.IsTrusted(input));
    }

    [Fact]
    public void IsTrusted_RejeitaExecutavelEmPastaGravavelPeloUsuario()
    {
        using var temp = new TempDirectory();
        string fake = temp.CreateFile("tools", "ffmpeg.exe");

        // %TEMP% pertence ao usuário e concede escrita a ele: um processo comum trocaria o .exe.
        Assert.False(TrustedPath.IsTrusted(fake));
    }

    // ------------------------------------------------------------------
    // TryResolveSystemTool
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("cmd.exe")]
    [InlineData("CMD.EXE")]
    public void TryResolveSystemTool_ResolveNoSystem32(string name)
    {
        Assert.True(TrustedPath.TryResolveSystemTool(name, out string full));
        Assert.Equal(Cmd, full, ignoreCase: true);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("cmd")]                    // sem .exe
    [InlineData("cmd.bat")]
    [InlineData(@"..\cmd.exe")]
    [InlineData(@"System32\cmd.exe")]
    [InlineData("System32/cmd.exe")]
    [InlineData("C:cmd.exe")]
    [InlineData(@"C:\Windows\System32\cmd.exe")]
    [InlineData("nul.exe")]
    [InlineData("c?d.exe")]
    [InlineData("c\"md.exe")]
    public void TryResolveSystemTool_RejeitaNomesInvalidos(string? name)
    {
        Assert.False(TrustedPath.TryResolveSystemTool(name, out string full));
        Assert.Equal("", full);
    }

    [Fact]
    public void TryResolveSystemTool_RejeitaFerramentaInexistente()
    {
        Assert.False(TrustedPath.TryResolveSystemTool($"radialflow-{Guid.NewGuid():N}.exe", out _));
    }
}
