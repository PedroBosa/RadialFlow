using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RadialFlow.Context;
using RadialFlow.Services;
using RadialFlow.Tests.TestSupport;
using Xunit;
using Logic = RadialFlow.Context.ActionRouter.Logic;

namespace RadialFlow.Tests;

/// <summary>
/// Regras do ActionRouter (ActionRouter.Logic). Nenhum teste toca no ActionRouter em si: o construtor
/// estático dele captura Application.Current.Dispatcher, que não existe fora do app.
/// </summary>
public sealed class ActionRouterTests : IDisposable
{
    private static readonly IntPtr Window = new(0x1234);

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private static ContextInfo Context(RadialContextKind kind, IntPtr window, params string[] paths) =>
        new(kind, window, "explorer", 10, 20, paths.Length == 0 ? ContextInfo.NoPaths : paths);

    // ------------------------------------------------------------------
    // Escopo de cada ação
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(RadialAction.SnapLeft, "TitleBar")]
    [InlineData(RadialAction.SnapRight, "TitleBar")]
    [InlineData(RadialAction.Maximize, "TitleBar")]
    [InlineData(RadialAction.Minimize, "TitleBar")]
    [InlineData(RadialAction.Close, "TitleBar")]
    [InlineData(RadialAction.AlwaysOnTop, "TitleBar")]
    [InlineData(RadialAction.Transparency, "TitleBar")]
    [InlineData(RadialAction.Convert, "FileSystem")]
    [InlineData(RadialAction.Compress, "FileSystem")]
    [InlineData(RadialAction.CopyPath, "FileSystem")]
    [InlineData(RadialAction.MoveTo, "FileSystem")]
    [InlineData(RadialAction.SendTo, "FileSystem")]
    [InlineData(RadialAction.Rename, "FileSystem")]
    [InlineData(RadialAction.Paste, "TextField")]
    [InlineData(RadialAction.Translate, "TextField")]
    [InlineData(RadialAction.Clear, "TextField")]
    [InlineData(RadialAction.SearchWeb, "TextField")]
    [InlineData(RadialAction.Format, "TextField")]
    [InlineData(RadialAction.FavoriteApps, "System")]
    [InlineData(RadialAction.SystemControls, "System")]
    [InlineData(RadialAction.ClipboardHistory, "System")]
    [InlineData(RadialAction.LockScreen, "System")]
    public void GetScope_ClassificaCadaAcao(RadialAction action, string expectedScope)
    {
        Assert.Equal(expectedScope, Logic.GetScope(action).ToString());
    }

    [Fact]
    public void GetScope_CobreTodasAsAcoesDoEnum()
    {
        foreach (RadialAction action in Enum.GetValues<RadialAction>())
            _ = Logic.GetScope(action);   // não lança
    }

    [Fact]
    public void GetScope_AcaoDesconhecida_Lanca()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Logic.GetScope((RadialAction)999));
    }

    // ------------------------------------------------------------------
    // TryValidate: o contexto precisa servir para a ação
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(RadialAction.Maximize)]
    [InlineData(RadialAction.Close)]
    [InlineData(RadialAction.Paste)]
    [InlineData(RadialAction.Clear)]
    public void TryValidate_AcaoDeJanelaSemJanela_EhRecusada(RadialAction action)
    {
        Assert.False(Logic.TryValidate(action, Context(RadialContextKind.TitleBar, IntPtr.Zero), out _, out string? reason));
        Assert.NotNull(reason);
    }

    [Fact]
    public void TryValidate_AcaoDeJanelaComJanela_MantemOContexto()
    {
        var ctx = Context(RadialContextKind.TitleBar, Window);

        Assert.True(Logic.TryValidate(RadialAction.Minimize, ctx, out var validated, out string? reason));
        Assert.Same(ctx, validated);
        Assert.Null(reason);
    }

    [Theory]
    [InlineData(RadialAction.LockScreen)]
    [InlineData(RadialAction.ClipboardHistory)]
    [InlineData(RadialAction.FavoriteApps)]
    public void TryValidate_AcaoDeSistema_NaoExigeJanelaNemCaminhos(RadialAction action)
    {
        Assert.True(Logic.TryValidate(action, Context(RadialContextKind.Generic, IntPtr.Zero), out _, out _));
    }

    [Fact]
    public void TryValidate_AcaoDesconhecida_EhRecusada()
    {
        Assert.False(Logic.TryValidate((RadialAction)999, Context(RadialContextKind.Generic, Window), out _, out string? reason));
        Assert.Contains("desconhecida", reason);
    }

    [Theory]
    [InlineData(RadialAction.Rename)]
    [InlineData(RadialAction.MoveTo)]
    [InlineData(RadialAction.Compress)]
    [InlineData(RadialAction.CopyPath)]
    public void TryValidate_AcaoDeArquivoSemCaminhos_EhRecusada(RadialAction action)
    {
        Assert.False(Logic.TryValidate(action, Context(RadialContextKind.FileOrFolder, Window), out _, out _));
    }

    [Theory]
    [InlineData(@"C:\Users\Public\..\..\Windows\System32\drivers\etc\hosts")]
    [InlineData(@"..\segredo.txt")]
    [InlineData(@"relativo\arquivo.txt")]
    [InlineData(@"C:\Users\Public\arquivo.txt:ads")]
    [InlineData(@"C:\Users\Public\CON")]
    [InlineData(@"\\?\C:\Windows\System32\config\SAM")]
    public void TryValidate_CaminhoInseguro_RecusaAAcaoInteira(string unsafePath)
    {
        var ctx = Context(RadialContextKind.FileOrFolder, Window, @"C:\Users\Public\ok.txt", unsafePath);

        Assert.False(Logic.TryValidate(RadialAction.Rename, ctx, out var validated, out string? reason));
        Assert.Same(ctx, validated);   // nada higienizado vaza num caso recusado
        Assert.Contains("inseguro", reason);
    }

    [Fact]
    public void TryValidate_CaminhosValidos_SaoCanonicalizadosEDeduplicados()
    {
        var ctx = Context(RadialContextKind.FileOrFolder, Window,
            @"C:/Users/Public/a.txt",
            @"C:\Users\Public\Fotos\",
            @"c:\users\public\A.TXT",          // mesmo item, outra caixa
            @"C:\Users\Public\b.txt");

        Assert.True(Logic.TryValidate(RadialAction.CopyPath, ctx, out var validated, out _));

        Assert.Equal(new[] { @"C:\Users\Public\a.txt", @"C:\Users\Public\Fotos", @"C:\Users\Public\b.txt" }, validated.Paths);
        Assert.Equal(ctx with { Paths = validated.Paths }, validated);   // só os caminhos mudam
    }

    [Fact]
    public void TryGetSafePaths_SelecaoGrandeDemais_EhRecusada()
    {
        var huge = Enumerable.Range(0, Logic.MaxPaths + 1).Select(i => $@"C:\Dados\{i}.txt").ToList();

        Assert.False(Logic.TryGetSafePaths(huge, out var safe, out string? reason));
        Assert.Empty(safe);
        Assert.Contains("grande", reason);
    }

    [Fact]
    public void TryGetSafePaths_Nulo_EhRecusado()
    {
        Assert.False(Logic.TryGetSafePaths(null, out _, out _));
    }

    // ------------------------------------------------------------------
    // Clipboard, URLs e terminais
    // ------------------------------------------------------------------

    [Fact]
    public void FormatPathsForClipboard_AspasEUmPorLinha()
    {
        Assert.Equal("\"C:\\a.txt\"", Logic.FormatPathsForClipboard([@"C:\a.txt"]));
        Assert.Equal($"\"C:\\a.txt\"{Environment.NewLine}\"C:\\b c.txt\"",
            Logic.FormatPathsForClipboard([@"C:\a.txt", @"C:\b c.txt"]));
    }

    [Fact]
    public void BuildSearchUrl_CodificaOTextoInteiro()
    {
        Assert.Equal("https://www.google.com/search?q=pre%C3%A7o%20%26%20prazo%3F", Logic.BuildSearchUrl("  preço & prazo?  "));
        Assert.Equal("https://www.google.com/search?q=a%20%26%20b", Logic.BuildSearchUrl(" a & b "));
    }

    [Fact]
    public void BuildTranslateUrl_UsaUtf8EDestinoPortugues()
    {
        Assert.Equal("https://translate.google.com/?sl=auto&tl=pt&text=ol%C3%A1%20mundo", Logic.BuildTranslateUrl("olá mundo"));
    }

    [Theory]
    [InlineData("texto \" com aspas")]
    [InlineData("linha 1\r\nlinha 2")]
    [InlineData("&tl=xx&q=injetado#frag")]
    [InlineData("javascript:alert(1)")]
    public void BuildUrls_SempreGeramUrlAceitaPeloLauncher(string text)
    {
        // Integração com o UnelevatedLauncher: o texto selecionado não consegue quebrar a linha de comando
        // nem trocar o esquema/host da URL.
        foreach (string url in new[] { Logic.BuildSearchUrl(text), Logic.BuildTranslateUrl(text) })
        {
            Assert.True(UnelevatedLauncher.IsSafeUrl(url));
            var uri = new Uri(url);
            Assert.Equal(Uri.UriSchemeHttps, uri.Scheme);
            Assert.EndsWith("google.com", uri.Host);
            Assert.Empty(uri.Fragment);
        }
    }

    [Fact]
    public void PrepareQuery_AparaELimitaOTamanho()
    {
        Assert.Equal("abc", Logic.PrepareQuery("  abc \r\n"));
        Assert.Equal("", Logic.PrepareQuery(null));
        Assert.Equal(Logic.MaxQueryChars, Logic.PrepareQuery(new string('x', Logic.MaxQueryChars * 3)).Length);
    }

    [Fact]
    public void PrepareQuery_NaoPartePardeSubstitutos()
    {
        // O emoji (2 chars) começa exatamente no último caractere permitido.
        string text = new string('a', Logic.MaxQueryChars - 1) + "😀" + "fim";

        string prepared = Logic.PrepareQuery(text);

        Assert.Equal(Logic.MaxQueryChars - 1, prepared.Length);
        Assert.False(char.IsHighSurrogate(prepared[^1]));
        _ = Uri.EscapeDataString(prepared);   // não lança com substituto órfão
    }

    [Theory]
    [InlineData("cmd", true)]
    [InlineData("PowerShell", true)]
    [InlineData("pwsh", true)]
    [InlineData("WindowsTerminal", true)]
    [InlineData("conhost", true)]
    [InlineData("OpenConsole", true)]
    [InlineData("notepad", false)]
    [InlineData("chrome", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsConsoleHost_IdentificaTerminais(string? processName, bool expected)
    {
        Assert.Equal(expected, Logic.IsConsoleHost(processName));
    }

    // ------------------------------------------------------------------
    // Transparência
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    [InlineData(2, 3)]
    [InlineData(3, 0)]
    [InlineData(-1, 1)]
    [InlineData(99, 1)]
    public void NextOpacityIndex_CiclaEToleraIndiceInvalido(int current, int expected)
    {
        Assert.Equal(expected, Logic.NextOpacityIndex(current));
    }

    [Fact]
    public void OpacityLevels_ComecaOpacoEDecresce()
    {
        Assert.Equal(255, Logic.OpacityLevels[0]);
        Assert.Equal(Logic.OpacityLevels.OrderByDescending(b => b), Logic.OpacityLevels);
    }

    // ------------------------------------------------------------------
    // Renomear
    // ------------------------------------------------------------------

    [Fact]
    public void TryBuildRenameTarget_NomeValido_FicaNaMesmaPasta()
    {
        string source = _temp.CreateFile("antigo.txt");

        Assert.True(Logic.TryBuildRenameTarget(source, "  novo nome.txt ", out string target, out string? error));
        Assert.Equal(_temp.Combine("novo nome.txt"), target);
        Assert.Null(error);
    }

    [Fact]
    public void TryBuildRenameTarget_PastaComSeparadorFinal()
    {
        string source = _temp.CreateDirectory("Fotos") + "\\";

        Assert.True(Logic.TryBuildRenameTarget(source, "Imagens", out string target, out _));
        Assert.Equal(_temp.Combine("Imagens"), target);
    }

    [Theory]
    [InlineData(@"..\..\Windows\System32\evil.dll")]
    [InlineData("../fora.txt")]
    [InlineData(@"sub\dentro.txt")]
    [InlineData(@"C:\Windows\x.txt")]
    [InlineData("arquivo.txt:ads")]
    [InlineData("CON")]
    [InlineData("nome.")]
    [InlineData("")]
    [InlineData(null)]
    public void TryBuildRenameTarget_NomeInvalido_EhRecusadoComMotivo(string? newName)
    {
        string source = _temp.CreateFile("antigo.txt");

        Assert.False(Logic.TryBuildRenameTarget(source, newName, out string target, out string? error));
        Assert.Equal("", target);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void TryBuildRenameTarget_OrigemInsegura_EhRecusada()
    {
        Assert.False(Logic.TryBuildRenameTarget(@"relativo\a.txt", "b.txt", out _, out _));
        Assert.False(Logic.TryBuildRenameTarget(Path.GetPathRoot(Environment.SystemDirectory)!, "b", out _, out _));
    }

    // ------------------------------------------------------------------
    // Compactar / extrair
    // ------------------------------------------------------------------

    [Fact]
    public void TryPlanArchive_UmArquivo_CompactaParaNomeZip()
    {
        string file = _temp.CreateFile("relatorio.docx");

        Assert.True(Logic.TryPlanArchive([file], out var plan, out _));
        Assert.Equal(ActionRouter.ArchiveMode.Compress, plan!.Mode);
        Assert.Equal(_temp.Combine("relatorio.zip"), plan.Output);
        Assert.Equal(new[] { file }, plan.Sources);
    }

    [Fact]
    public void TryPlanArchive_ZipExistente_NuncaSobrescreve()
    {
        string file = _temp.CreateFile("relatorio.docx");
        _temp.CreateFile("relatorio.zip");

        Assert.True(Logic.TryPlanArchive([file], out var plan, out _));
        Assert.Equal(_temp.Combine("relatorio (1).zip"), plan!.Output);
    }

    [Fact]
    public void TryPlanArchive_VariosItens_UsaSufixoCompactado()
    {
        string a = _temp.CreateFile("a.txt");
        string b = _temp.CreateDirectory("Pasta");

        Assert.True(Logic.TryPlanArchive([a, b], out var plan, out _));
        Assert.Equal(ActionRouter.ArchiveMode.Compress, plan!.Mode);
        Assert.Equal(_temp.Combine("a_compactado.zip"), plan.Output);
        Assert.Equal(new[] { a, b }, plan.Sources);
    }

    [Fact]
    public void TryPlanArchive_Pasta_MantemONomeInteiro()
    {
        string folder = _temp.CreateDirectory("versao.1.2");

        Assert.True(Logic.TryPlanArchive([folder], out var plan, out _));
        Assert.Equal(_temp.Combine("versao.1.2.zip"), plan!.Output);
    }

    [Fact]
    public void TryPlanArchive_UmZip_ExtraiParaPastaLivre()
    {
        string zip = _temp.CreateFile("pacote.zip");
        _temp.CreateDirectory("pacote");   // já existe: não mescla

        Assert.True(Logic.TryPlanArchive([zip], out var plan, out _));
        Assert.Equal(ActionRouter.ArchiveMode.Extract, plan!.Mode);
        Assert.Equal(_temp.Combine("pacote (1)"), plan.Output);
    }

    [Fact]
    public void TryPlanArchive_PastaTerminadaEmZip_EhCompactadaNaoExtraida()
    {
        string folder = _temp.CreateDirectory("backup.zip");

        Assert.True(Logic.TryPlanArchive([folder], out var plan, out _));
        Assert.Equal(ActionRouter.ArchiveMode.Compress, plan!.Mode);
        Assert.Equal(_temp.Combine("backup.zip.zip"), plan.Output);
    }

    [Fact]
    public void TryPlanArchive_ZipEntreOutros_Compacta()
    {
        string zip = _temp.CreateFile("pacote.zip");
        string other = _temp.CreateFile("leia-me.txt");

        Assert.True(Logic.TryPlanArchive([zip, other], out var plan, out _));
        Assert.Equal(ActionRouter.ArchiveMode.Compress, plan!.Mode);
    }

    [Fact]
    public void TryPlanArchive_CaminhoInseguroOuRaiz_EhRecusado()
    {
        Assert.False(Logic.TryPlanArchive([@"C:\Temp\..\Windows\x.txt"], out var plan, out string? error));
        Assert.Null(plan);
        Assert.NotNull(error);

        Assert.False(Logic.TryPlanArchive([Path.GetPathRoot(Environment.SystemDirectory)!], out _, out _));
        Assert.False(Logic.TryPlanArchive([], out _, out _));
    }

    // ------------------------------------------------------------------
    // Conversão direta (sub-fatia)
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("webp", "webp")]
    [InlineData(".PNG", "png")]
    [InlineData("  Mp3 ", "mp3")]
    [InlineData("m4a", "m4a")]
    [InlineData("", "")]
    [InlineData(null, "")]
    [InlineData("../exe", "")]
    [InlineData(@"png\..\..\x", "")]
    [InlineData("p n g", "")]
    [InlineData("png;calc", "")]
    [InlineData("formatolongo", "")]
    public void NormalizeFormat_AceitaSoExtensaoSimples(string? format, string expected)
    {
        Assert.Equal(expected, Logic.NormalizeFormat(format));
    }

    [Fact]
    public void TryBuildConversionDestination_MesmaPastaNovaExtensaoSemSobrescrever()
    {
        string source = _temp.CreateFile("foto.png");

        Assert.True(Logic.TryBuildConversionDestination(source, ".WEBP", out string first));
        Assert.Equal(_temp.Combine("foto.webp"), first);

        _temp.CreateFile("foto.webp");
        Assert.True(Logic.TryBuildConversionDestination(source, "webp", out string second));
        Assert.Equal(_temp.Combine("foto (1).webp"), second);
    }

    [Theory]
    [InlineData("../../exe")]
    [InlineData("")]
    [InlineData("png:ads")]
    public void TryBuildConversionDestination_FormatoInvalido_EhRecusado(string format)
    {
        string source = _temp.CreateFile("foto.png");

        Assert.False(Logic.TryBuildConversionDestination(source, format, out string destination));
        Assert.Equal("", destination);
    }

    [Fact]
    public void TryBuildConversionDestination_OrigemInsegura_EhRecusada()
    {
        Assert.False(Logic.TryBuildConversionDestination(@"..\foto.png", "jpg", out _));
    }

    // ------------------------------------------------------------------
    // Mover
    // ------------------------------------------------------------------

    [Fact]
    public void PlanMove_DestinoLivre_EstaPronto()
    {
        string source = _temp.CreateFile("origem", "a.txt");
        string destFolder = _temp.CreateDirectory("destino");

        var plan = Logic.PlanMove(source, destFolder);

        Assert.Equal(ActionRouter.MoveDecision.Ready, plan.Decision);
        Assert.Equal(Path.Combine(destFolder, "a.txt"), plan.Destination);
        Assert.Equal("a.txt", plan.Name);
    }

    [Fact]
    public void PlanMove_DestinoOcupado_EhConflito()
    {
        string source = _temp.CreateFile("origem", "a.txt");
        _temp.CreateFile("destino", "a.txt");

        Assert.Equal(ActionRouter.MoveDecision.Conflict, Logic.PlanMove(source, _temp.Combine("destino")).Decision);
    }

    [Fact]
    public void PlanMove_JaEstaNaPasta_NaoFazNada()
    {
        string source = _temp.CreateFile("a.txt");

        Assert.Equal(ActionRouter.MoveDecision.AlreadyThere, Logic.PlanMove(source, _temp.Root).Decision);
    }

    [Fact]
    public void PlanMove_PastaParaDentroDelaMesma_EhImpossivel()
    {
        string folder = _temp.CreateDirectory("Projeto");
        string inside = _temp.CreateDirectory("Projeto", "sub");

        Assert.Equal(ActionRouter.MoveDecision.IntoItself, Logic.PlanMove(folder, inside).Decision);
    }

    [Fact]
    public void PlanMove_PastaComPrefixoParecido_NaoEhConfundidaComSubpasta()
    {
        string folder = _temp.CreateDirectory("Projeto");
        string sibling = _temp.CreateDirectory("Projeto2");

        Assert.Equal(ActionRouter.MoveDecision.Ready, Logic.PlanMove(folder, sibling).Decision);
    }

    [Fact]
    public void PlanMove_RaizDoVolume_EhInvalida()
    {
        string root = Path.GetPathRoot(Environment.SystemDirectory)!;

        Assert.Equal(ActionRouter.MoveDecision.Invalid, Logic.PlanMove(root, _temp.Root).Decision);
    }

    [Theory]
    [InlineData(@"C:\a\b", @"C:\a", true)]
    [InlineData(@"C:\a", @"C:\a", true)]
    [InlineData(@"C:\a\", @"C:\a", true)]
    [InlineData(@"C:\A\B", @"c:\a\", true)]
    [InlineData(@"C:\ab", @"C:\a", false)]
    [InlineData(@"C:\a", @"C:\a\b", false)]
    [InlineData(@"D:\a", @"C:\a", false)]
    public void IsSameOrInside_ComparaPorSegmentos(string candidate, string folder, bool expected)
    {
        Assert.Equal(expected, Logic.IsSameOrInside(candidate, folder));
    }

    // ------------------------------------------------------------------
    // Integração: fatias do contexto detectado → validação do roteador
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(RadialContextKind.FileOrFolder, "FileSystem")]
    [InlineData(RadialContextKind.TitleBar, "TitleBar")]
    [InlineData(RadialContextKind.TextField, "TextField")]
    [InlineData(RadialContextKind.DesktopBackground, "System")]
    [InlineData(RadialContextKind.Generic, "System")]
    public void Integracao_FatiasDeCadaContextoGeramAcoesQueORoteadorAceita(RadialContextKind kind, string expectedScope)
    {
        string file = _temp.CreateFile("foto.png");
        var ctx = Context(kind, Window, file);
        var actions = new RecordingSliceActions();
        var deps = new SliceFactoryDependencies(actions,
            _ => [new ConversionOption("jpg", "JPEG", "WIC"), new ConversionOption("webp", "WEBP", "ImageMagick")],
            () => [new RadialFlow.Config.FavoriteAppEntry("Calc", "calc.exe")],
            File.Exists);

        var slices = SliceFactory.Build(ctx, deps);
        foreach (var slice in slices)
        {
            slice.Execute!();
            if (slice.SubSlices is { } subSlices)
            {
                foreach (var sub in subSlices) sub.Execute!();
            }
        }

        Assert.Equal(slices.Count, actions.Runs.Count);
        Assert.All(actions.Runs, run =>
        {
            Assert.Equal(expectedScope, Logic.GetScope(run.Action).ToString());
            Assert.True(Logic.TryValidate(run.Action, run.Context, out _, out string? reason), reason);
        });

        // Sub-fatias de conversão também precisam gerar um destino válido.
        Assert.All(actions.Conversions, c =>
            Assert.True(Logic.TryBuildConversionDestination(c.Source, c.Format, out _)));
    }

    [Fact]
    public void Integracao_ContextoComCaminhoForjado_GeraFatiasMasORoteadorRecusa()
    {
        // Ex.: janela falsa de classe "CabinetWClass" devolvendo um nome com travessia via UI Automation.
        var ctx = Context(RadialContextKind.FileOrFolder, Window, @"C:\Users\Public\Desktop\..\..\..\Windows\System32\config");
        var actions = new RecordingSliceActions();
        var deps = new SliceFactoryDependencies(actions, _ => [], () => null, _ => false);

        foreach (var slice in SliceFactory.Build(ctx, deps)) slice.Execute!();

        Assert.NotEmpty(actions.Runs);
        Assert.All(actions.Runs, run => Assert.False(Logic.TryValidate(run.Action, run.Context, out _, out _)));
    }
}
