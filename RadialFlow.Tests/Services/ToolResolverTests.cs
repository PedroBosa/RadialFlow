using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using RadialFlow.Services;
using RadialFlow.Tests.TestSupport;
using Xunit;

namespace RadialFlow.Tests;

public sealed class ToolResolverTests : IDisposable
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

    private readonly TempDirectory _temp = new();
    private readonly HashSet<string> _trusted = new(StringComparer.OrdinalIgnoreCase);
    private readonly ManualTimeProvider _clock = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    private readonly List<string> _trustQueries = new();

    private string BaseDir => _temp.Combine("app");
    private string ProgramFiles => _temp.Combine("ProgramFiles");
    private string PathDirA => _temp.Combine("path-a");
    private string PathDirB => _temp.Combine("path-b");

    public ToolResolverTests()
    {
        _temp.CreateDirectory("app");
        _temp.CreateDirectory("ProgramFiles");
        _temp.CreateDirectory("path-a");
        _temp.CreateDirectory("path-b");
    }

    public void Dispose() => _temp.Dispose();

    /// <summary>Localizador isolado: só enxerga a sandbox; confiança = lista explícita do teste.</summary>
    private ToolLocator CreateLocator(string? pathVariable = null, IEnumerable<string>? programFilesRoots = null)
    {
        var environment = new ToolSearchEnvironment(
            BaseDir,
            (programFilesRoots ?? [ProgramFiles]).ToArray(),
            pathVariable ?? PathDirA,
            path =>
            {
                lock (_trustQueries) _trustQueries.Add(path);
                return _trusted.Contains(path);
            },
            _clock,
            Ttl);

        return new ToolLocator(environment);
    }

    private string AddTool(bool trusted, params string[] parts)
    {
        string file = _temp.CreateFile(parts);
        if (trusted) _trusted.Add(file);
        return file;
    }

    // ------------------------------------------------------------------
    // Ordem de busca
    // ------------------------------------------------------------------

    [Fact]
    public void EnumerateCandidates_SegueAOrdemDocumentada()
    {
        _temp.CreateDirectory("ProgramFiles", "ImageMagick-7.1.1-Q16-HDRI");
        string pathVariable = string.Join(';', PathDirA, @"relativo\pasta", "", "  ", "C:sem-raiz", $"\"{PathDirB}\"");

        var candidates = CreateLocator(pathVariable).EnumerateCandidates("magick.exe").ToList();

        Assert.Equal(new[]
        {
            Path.Combine(BaseDir, "magick.exe"),
            Path.Combine(BaseDir, "tools", "magick.exe"),
            Path.Combine(ProgramFiles, "ffmpeg", "bin", "magick.exe"),
            Path.Combine(ProgramFiles, "Pandoc", "magick.exe"),
            Path.Combine(ProgramFiles, "LibreOffice", "program", "magick.exe"),
            Path.Combine(ProgramFiles, "ImageMagick-7.1.1-Q16-HDRI", "magick.exe"),
            Path.Combine(PathDirA, "magick.exe"),
            Path.Combine(PathDirB, "magick.exe"),   // aspas removidas
        }, candidates);
    }

    [Fact]
    public void EnumerateCandidates_IgnoraEntradasRelativasDoPath()
    {
        var candidates = CreateLocator(@"relativo;.\local;..\acima;C:sem-raiz").EnumerateCandidates("ffmpeg.exe").ToList();

        // Só sobram candidatos da sandbox (pasta do app + Arquivos de Programas): nenhum relativo ao diretório atual.
        Assert.Equal(5, candidates.Count);
        Assert.All(candidates, c => Assert.StartsWith(_temp.Root, c, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void EnumerateCandidates_IgnoraRaizDeProgramasInexistente()
    {
        string missingRoot = _temp.Combine("nao-existe");

        var candidates = CreateLocator(programFilesRoots: [missingRoot]).EnumerateCandidates("ffmpeg.exe");

        Assert.DoesNotContain(candidates, c => c.StartsWith(missingRoot, StringComparison.OrdinalIgnoreCase));
    }

    // ------------------------------------------------------------------
    // Validação (só ferramentas confiáveis)
    // ------------------------------------------------------------------

    [Fact]
    public void FindTool_PulaCandidatoNaoConfiavelEDevolveOProximoConfiavel()
    {
        string untrusted = AddTool(false, "app", "tools", "ffmpeg.exe");    // vem antes na ordem
        string trusted = AddTool(true, "ProgramFiles", "ffmpeg", "bin", "ffmpeg.exe");
        var locator = CreateLocator();

        Assert.Equal(trusted, locator.FindTool("ffmpeg.exe"));
        Assert.Contains(untrusted, _trustQueries);   // foi avaliado e recusado
    }

    [Fact]
    public void FindTool_SoNaoConfiavel_DevolveNullEExplicaOMotivo()
    {
        string untrusted = AddTool(false, "app", "tools", "pandoc.exe");
        var locator = CreateLocator();

        Assert.Null(locator.FindTool("pandoc.exe"));

        string message = locator.DescribeMissing("pandoc.exe");
        Assert.Contains(untrusted, message);
        Assert.Contains("ignorado por segurança", message);
    }

    [Fact]
    public void FindTool_NaoEncontrada_DevolveNullComMensagemDeInstalacao()
    {
        var locator = CreateLocator();

        Assert.Null(locator.FindTool("soffice.exe"));
        Assert.Contains("não foi encontrado", locator.DescribeMissing("soffice.exe"));
    }

    [Fact]
    public void FindTool_EncontraNoPathQuandoConfiavel()
    {
        string trusted = AddTool(true, "path-a", "vips.exe");

        Assert.Equal(trusted, CreateLocator().FindTool("vips.exe"));
    }

    [Fact]
    public void FindTool_EncontraImageMagickEmPastaVersionada()
    {
        string trusted = AddTool(true, "ProgramFiles", "ImageMagick-7.1.1-Q16-HDRI", "magick.exe");

        Assert.Equal(trusted, CreateLocator().FindTool("magick.exe"));
    }

    [Fact]
    public void FindTool_NaoAvaliaCandidatosInexistentes()
    {
        AddTool(true, "ProgramFiles", "Pandoc", "pandoc.exe");

        CreateLocator().FindTool("pandoc.exe");

        Assert.All(_trustQueries, p => Assert.True(File.Exists(p)));
    }

    [Theory]
    [InlineData(@"..\ffmpeg.exe")]
    [InlineData(@"sub\ffmpeg.exe")]
    [InlineData("sub/ffmpeg.exe")]
    [InlineData("C:ffmpeg.exe")]
    [InlineData("ff*.exe")]
    [InlineData("nul")]
    [InlineData("..")]
    [InlineData("")]
    [InlineData("   ")]
    public void FindTool_RejeitaNomeDeFerramentaInvalido(string toolName)
    {
        AddTool(true, "app", "ffmpeg.exe");

        Assert.Null(CreateLocator().FindTool(toolName));
        Assert.Empty(_trustQueries);
    }

    // ------------------------------------------------------------------
    // Caminho configurado pelo usuário
    // ------------------------------------------------------------------

    [Fact]
    public void FindTool_CaminhoConfiguradoConfiavel_TemPrioridade()
    {
        AddTool(true, "ProgramFiles", "ffmpeg", "bin", "ffmpeg.exe");
        string configured = AddTool(true, "custom", "ffmpeg.exe");

        Assert.Equal(configured, CreateLocator().FindTool("ffmpeg.exe", configured));
    }

    [Fact]
    public void FindTool_CaminhoConfiguradoNaoConfiavel_EhIgnoradoERegistrado()
    {
        string fallback = AddTool(true, "ProgramFiles", "ffmpeg", "bin", "ffmpeg.exe");
        string configured = AddTool(false, "custom", "ffmpeg.exe");
        var locator = CreateLocator();

        Assert.Equal(fallback, locator.FindTool("ffmpeg.exe", configured));
        Assert.Contains(configured, locator.DescribeMissing("ffmpeg.exe"));
    }

    [Fact]
    public void FindTool_CaminhoConfiguradoComTravessia_NuncaEhUsado()
    {
        AddTool(true, "custom", "ffmpeg.exe");
        string traversal = Path.Combine(BaseDir, "..", "custom", "ffmpeg.exe");
        _trusted.Add(traversal);   // nem um veredito "confiável" salva um caminho com ".."

        Assert.Null(CreateLocator().FindTool("ffmpeg.exe", traversal));
    }

    [Fact]
    public void FindTool_CaminhoConfiguradoRelativo_NuncaEhUsado()
    {
        Assert.Null(CreateLocator().FindTool("ffmpeg.exe", @"custom\ffmpeg.exe"));
        Assert.DoesNotContain(_trustQueries, p => p.EndsWith(@"custom\ffmpeg.exe", StringComparison.OrdinalIgnoreCase));
    }

    // ------------------------------------------------------------------
    // Cache
    // ------------------------------------------------------------------

    [Fact]
    public void FindTool_NaoEncontrado_FicaEmCacheAteOTtlExpirar()
    {
        var locator = CreateLocator();
        Assert.Null(locator.FindTool("pandoc.exe"));

        string installed = AddTool(true, "ProgramFiles", "Pandoc", "pandoc.exe");

        _clock.Advance(Ttl - TimeSpan.FromSeconds(1));
        Assert.Null(locator.FindTool("pandoc.exe"));          // ainda dentro do TTL

        _clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(installed, locator.FindTool("pandoc.exe"));
    }

    [Fact]
    public void FindTool_Encontrado_ReavaliaQuandoOArquivoSome()
    {
        string first = AddTool(true, "app", "ffmpeg.exe");
        string second = AddTool(true, "ProgramFiles", "ffmpeg", "bin", "ffmpeg.exe");
        var locator = CreateLocator();

        Assert.Equal(first, locator.FindTool("ffmpeg.exe"));

        File.Delete(first);
        Assert.Equal(second, locator.FindTool("ffmpeg.exe"));
    }

    [Fact]
    public void FindTool_Encontrado_UsaCacheSemReavaliarConfianca()
    {
        AddTool(true, "ProgramFiles", "ffmpeg", "bin", "ffmpeg.exe");
        var locator = CreateLocator();
        locator.FindTool("ffmpeg.exe");
        int queries = _trustQueries.Count;

        locator.FindTool("ffmpeg.exe");

        Assert.Equal(queries, _trustQueries.Count);
    }

    [Fact]
    public void ResetCache_DescartaResultadoNegativoImediatamente()
    {
        var locator = CreateLocator();
        Assert.Null(locator.FindTool("soffice.exe"));

        string installed = AddTool(true, "ProgramFiles", "LibreOffice", "program", "soffice.exe");
        locator.ResetCache();

        Assert.Equal(installed, locator.FindTool("soffice.exe"));
    }

    [Fact]
    public void FindTool_ChamadasConcorrentes_DevolvemOMesmoResultado()
    {
        string trusted = AddTool(true, "ProgramFiles", "ffmpeg", "bin", "ffmpeg.exe");
        var locator = CreateLocator();
        var results = new string?[64];

        Parallel.For(0, results.Length, i =>
        {
            locator.ResetCache();
            results[i] = locator.FindTool("ffmpeg.exe");
        });

        Assert.All(results, r => Assert.Equal(trusted, r));
    }

    // ------------------------------------------------------------------
    // Ambiente real e fachada estática
    // ------------------------------------------------------------------

    [Fact]
    public void CreateDefault_UsaRaizesDeProgramasERelogioDoSistema()
    {
        var environment = ToolSearchEnvironment.CreateDefault();

        Assert.NotEmpty(environment.ProgramFilesRoots);
        Assert.Equal(environment.ProgramFilesRoots.Count,
            environment.ProgramFilesRoots.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Same(TimeProvider.System, environment.Clock);
        Assert.Equal(ToolSearchEnvironment.DefaultMissingTtl, environment.MissingTtl);
        Assert.Equal(AppContext.BaseDirectory, environment.BaseDirectory);
    }

    [Fact]
    public void Fachada_RejeitaNomeInvalidoEFerramentaInexistente()
    {
        Assert.Null(ToolResolver.FindTool(@"..\..\Windows\System32\cmd.exe"));
        Assert.Null(ToolResolver.FindTool($"radialflow-{Guid.NewGuid():N}.exe"));
    }

    [Fact]
    public void Fachada_ResolveFerramentaConfiguradaDoSystem32()
    {
        string cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");

        // Caminho configurado + TrustedPath.IsTrusted real (System32 é protegido).
        Assert.Equal(cmd, ToolResolver.FindTool("cmd.exe", cmd), ignoreCase: true);
    }
}
