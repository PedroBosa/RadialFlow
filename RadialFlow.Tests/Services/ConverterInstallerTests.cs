using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using RadialFlow.Services;
using RadialFlow.Tests.TestSupport;
using Xunit;

namespace RadialFlow.Tests;

/// <summary>
/// Catálogo e instalação dos conversores. A rede e o instalador do Windows são dublês: nada é baixado nem instalado.
/// A extração do zip é real (zip montado no teste).
/// </summary>
public sealed class ConverterInstallerTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    // ------------------------------------------------------------------
    // Dublês
    // ------------------------------------------------------------------

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(respond(request));
        }
    }

    /// <summary>Corpo que nunca entrega nada (conexão "pendurada").</summary>
    private sealed class HangingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }

    private static HttpResponseMessage Ok(byte[] body) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

    private static ConverterPackage Package(byte[] content, PackageKind kind = PackageKind.Msi, string id = "teste",
        string? sha = null, long? size = null, string url = "https://example.test/pacote/teste-1.0.msi") =>
        new(id, "Teste", "para testes", "1.0", new Uri(url),
            sha ?? Convert.ToHexStringLower(SHA256.HashData(content)), size ?? content.Length, kind, id + ".exe", "MIT");

    private ConverterInstaller Installer(HttpMessageHandler handler, Func<string, IReadOnlyList<string>, Task<int>>? run = null,
        Func<ConverterPackage, bool>? installed = null, TimeSpan? stall = null) =>
        new(new ConverterInstallerOptions
        {
            ToolsDirectory = _temp.Combine("tools"),
            DownloadsDirectory = _temp.Combine("tools", "downloads"),
            FailureLogDirectory = _temp.Combine("logs"),
            Handler = handler,
            RunInstaller = run ?? ((_, _) => Task.FromResult(0)),
            IsInstalled = installed ?? (_ => true),
            StallTimeout = stall ?? TimeSpan.FromSeconds(30)
        });

    private static readonly byte[] Content = Enumerable.Range(0, 300_000).Select(i => (byte)(i * 7)).ToArray();

    // ------------------------------------------------------------------
    // Catálogo
    // ------------------------------------------------------------------

    [Fact]
    public void Catalogo_FontesOficiaisPorHttps_ComHashESize()
    {
        string[] officialHosts = ["github.com", "downloadarchive.documentfoundation.org"];

        Assert.Equal(new[] { "ffmpeg", "pandoc", "imagemagick", "libreoffice" }, ConverterPackages.All.Select(p => p.Id));
        Assert.All(ConverterPackages.All, p =>
        {
            Assert.Equal(Uri.UriSchemeHttps, p.Url.Scheme);
            Assert.Contains(p.Url.Host, officialHosts);
            Assert.Matches("^[0-9a-f]{64}$", p.Sha256);
            Assert.InRange(p.SizeBytes, 1_000_000, 1_000_000_000);
            Assert.Contains(p.Version, p.Url.AbsoluteUri.Replace("26.8.1.1", "26.8.1", StringComparison.Ordinal));
            Assert.EndsWith(".exe", p.ToolExe, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(p.Purpose));
        });
        Assert.EndsWith(".zip", ConverterPackages.FFmpeg.FileName, StringComparison.Ordinal);
        Assert.EndsWith(".msi", ConverterPackages.Pandoc.FileName, StringComparison.Ordinal);
        Assert.EndsWith(".msi", ConverterPackages.LibreOffice.FileName, StringComparison.Ordinal);
        Assert.EndsWith(".exe", ConverterPackages.ImageMagick.FileName, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_AceitaMaiusculasEspacosERepetidos_NaOrdemDoCatalogo()
    {
        var chosen = ConverterPackages.Parse(" libreoffice, FFMPEG;ffmpeg pandoc,xyz", out var unknown);

        Assert.Equal(new[] { "ffmpeg", "pandoc", "libreoffice" }, chosen.Select(p => p.Id));
        Assert.Equal(new[] { "xyz" }, unknown);
        Assert.Empty(ConverterPackages.Parse(null, out _));
    }

    [Fact]
    public void Argumentos_Msi_ParaTodosOsUsuariosSemInterfaceComLog()
    {
        var args = ConverterPackages.InstallerArguments(ConverterPackages.Pandoc, @"C:\d\p.msi", @"C:\d\p.log");

        Assert.Equal(new[] { "/i", @"C:\d\p.msi", "/qn", "/norestart", "ALLUSERS=1", "/l*v", @"C:\d\p.log" }, args);
    }

    [Fact]
    public void Argumentos_LibreOffice_NaoTomaOLugarDoOfficeNemCriaAtalhos()
    {
        var args = ConverterPackages.InstallerArguments(ConverterPackages.LibreOffice, @"C:\d\lo.msi", @"C:\d\lo.log");

        Assert.Contains("ALLUSERS=1", args);
        Assert.Contains("REGISTER_NO_MSO_TYPES=1", args);
        Assert.Contains("CREATEDESKTOPLINK=0", args);
        Assert.Contains("ISCHECKFORPRODUCTUPDATES=0", args);
        Assert.Contains("QUICKSTART=0", args);
        Assert.Contains("RebootYesNo=No", args);
    }

    [Fact]
    public void Argumentos_Inno_SilenciosoParaTodos()
    {
        var args = ConverterPackages.InstallerArguments(ConverterPackages.ImageMagick, @"C:\d\im.exe", @"C:\d\im.log");

        Assert.Equal(new[] { "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/SP-", "/ALLUSERS", @"/LOG=C:\d\im.log" }, args);
        Assert.Throws<ArgumentOutOfRangeException>(() => ConverterPackages.InstallerArguments(ConverterPackages.FFmpeg, "a", "b"));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(3010, true)]
    [InlineData(1641, true)]
    [InlineData(1603, false)]
    [InlineData(1618, false)]
    [InlineData(-1, false)]
    public void CodigoDeSaida_ReinicioPendenteContaComoSucesso(int code, bool success)
    {
        Assert.Equal(success, ConverterPackages.IsSuccessExitCode(code));
    }

    [Fact]
    public void CodigoDeSaida_OutraInstalacaoEmAndamento_TemMensagemClara()
    {
        Assert.Contains("outra instalação", ConverterPackages.DescribeExitCode(ConverterPackages.Pandoc, 1618));
        Assert.Contains("1603", ConverterPackages.DescribeExitCode(ConverterPackages.Pandoc, 1603));
    }

    // ------------------------------------------------------------------
    // Download conferido
    // ------------------------------------------------------------------

    [Fact]
    public async Task Download_HashCerto_GravaOArquivoInteiroEInformaProgresso()
    {
        var progress = new List<(long Received, long Total)>();
        string dest = _temp.Combine("baixado.msi");

        await Installer(new FakeHandler(_ => Ok(Content))).DownloadVerifiedAsync(Package(Content), dest, (r, t) => progress.Add((r, t)), CancellationToken.None);

        Assert.Equal(Content, File.ReadAllBytes(dest));
        Assert.False(File.Exists(dest + ".partial"));
        Assert.Equal(Content.Length, progress[^1].Received);
        Assert.All(progress, p => Assert.Equal(Content.Length, p.Total));
        Assert.True(progress.Zip(progress.Skip(1)).All(pair => pair.Second.Received > pair.First.Received));
    }

    [Fact]
    public async Task Download_ArquivoAlterado_NaoGravaNada()
    {
        byte[] tampered = (byte[])Content.Clone();
        tampered[1000] ^= 0xFF;
        string dest = _temp.Combine("baixado.msi");

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            Installer(new FakeHandler(_ => Ok(tampered))).DownloadVerifiedAsync(Package(Content), dest, null, CancellationToken.None));

        Assert.Contains("SHA-256", error.Message);
        Assert.False(File.Exists(dest));
        Assert.False(File.Exists(dest + ".partial"));
    }

    [Fact]
    public async Task Download_MaiorQueOEsperado_ParaNaHora()
    {
        string dest = _temp.Combine("baixado.msi");

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            Installer(new FakeHandler(_ => Ok(Content))).DownloadVerifiedAsync(Package(Content, size: 1000), dest, null, CancellationToken.None));

        Assert.Contains("maior", error.Message);
        Assert.False(File.Exists(dest + ".partial"));
    }

    [Fact]
    public async Task Download_Incompleto_Falha()
    {
        string dest = _temp.Combine("baixado.msi");

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            Installer(new FakeHandler(_ => Ok(Content[..1000]))).DownloadVerifiedAsync(Package(Content), dest, null, CancellationToken.None));

        Assert.Contains("incompleto", error.Message);
        Assert.False(File.Exists(dest));
    }

    [Fact]
    public async Task Download_SoHttps()
    {
        var handler = new FakeHandler(_ => Ok(Content));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Installer(handler).DownloadVerifiedAsync(Package(Content, url: "http://example.test/x.msi"), _temp.Combine("x.msi"), null, CancellationToken.None));

        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Download_SiteRespondeErro_MensagemComOCodigo()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Installer(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)))
                .DownloadVerifiedAsync(Package(Content), _temp.Combine("x.msi"), null, CancellationToken.None));

        Assert.Contains("404", error.Message);
    }

    [Fact]
    public async Task Download_SemInternet_MensagemClara()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Installer(new FakeHandler(_ => throw new HttpRequestException("DNS")))
                .DownloadVerifiedAsync(Package(Content), _temp.Combine("x.msi"), null, CancellationToken.None));

        Assert.Contains("internet", error.Message);
    }

    [Fact]
    public async Task Download_ConexaoPendurada_DesisteNoPrazo()
    {
        var hanging = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new HangingStream()) });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Installer(hanging, stall: TimeSpan.FromMilliseconds(300)).DownloadVerifiedAsync(Package(Content), _temp.Combine("x.msi"), null, CancellationToken.None));

        Assert.Contains("parou", error.Message);
        Assert.False(File.Exists(_temp.Combine("x.msi.partial")));
    }

    [Fact]
    public async Task Download_CanceladoNoMeio_NaoDeixaArquivo()
    {
        using var cts = new CancellationTokenSource();
        string dest = _temp.Combine("x.msi");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Installer(new FakeHandler(_ => Ok(Content))).DownloadVerifiedAsync(Package(Content), dest, (_, _) => cts.Cancel(), cts.Token));

        Assert.False(File.Exists(dest));
        Assert.False(File.Exists(dest + ".partial"));
    }

    // ------------------------------------------------------------------
    // Extração do FFmpeg
    // ------------------------------------------------------------------

    private string MakeZip(params (string Name, string Text)[] entries)
    {
        string path = _temp.Combine($"pacote-{Guid.NewGuid():N}.zip");
        using (ZipArchive zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            foreach (var (name, text) in entries)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write(text);
            }
        }
        return path;
    }

    [Fact]
    public void ExtrairFfmpeg_SoOsExecutaveisEALicenca_NadaSaiDaPasta()
    {
        string zip = MakeZip(
            ("ffmpeg-9.0.1-essentials_build/bin/ffmpeg.exe", "ffmpeg"),
            ("ffmpeg-9.0.1-essentials_build/bin/ffprobe.exe", "ffprobe"),
            ("ffmpeg-9.0.1-essentials_build/LICENSE", "GPL"),
            ("ffmpeg-9.0.1-essentials_build/doc/ffmpeg.html", "doc"),
            ("ffmpeg-9.0.1-essentials_build/bin/../../fora.exe", "x"),
            ("../escapou.exe", "x"),
            ("ffmpeg-9.0.1-essentials_build/bin/sub/aninhado.exe", "x"));
        string target = _temp.Combine("tools", "ffmpeg");

        ConverterInstaller.ExtractFfmpeg(zip, target);

        Assert.Equal(new[] { "ffmpeg.exe", "ffprobe.exe", "LICENSE.txt" }.Order(), Directory.GetFiles(target).Select(Path.GetFileName).Order());
        Assert.Equal("ffmpeg", File.ReadAllText(Path.Combine(target, "ffmpeg.exe")));
        Assert.Empty(Directory.GetFiles(_temp.Root, "*.exe", SearchOption.TopDirectoryOnly));
        Assert.Equal(new[] { "ffmpeg" }, Directory.GetDirectories(_temp.Combine("tools")).Select(Path.GetFileName));   // sem sobra temporária
    }

    [Fact]
    public void ExtrairFfmpeg_TrocaAVersaoAnteriorInteira()
    {
        string target = _temp.CreateDirectory("tools", "ffmpeg");
        File.WriteAllText(Path.Combine(target, "ffmpeg.exe"), "antigo");
        File.WriteAllText(Path.Combine(target, "ffplay.exe"), "antigo");

        ConverterInstaller.ExtractFfmpeg(MakeZip(("f/bin/ffmpeg.exe", "novo")), target);

        Assert.Equal("novo", File.ReadAllText(Path.Combine(target, "ffmpeg.exe")));
        Assert.False(File.Exists(Path.Combine(target, "ffplay.exe")));
    }

    [Fact]
    public void ExtrairFfmpeg_PacoteSemFfmpeg_MantemOQueJaEstavaInstalado()
    {
        string target = _temp.CreateDirectory("tools", "ffmpeg");
        File.WriteAllText(Path.Combine(target, "ffmpeg.exe"), "antigo");

        Assert.Throws<InvalidDataException>(() => ConverterInstaller.ExtractFfmpeg(MakeZip(("f/bin/outro.exe", "x")), target));

        Assert.Equal("antigo", File.ReadAllText(Path.Combine(target, "ffmpeg.exe")));
        Assert.Equal(new[] { "ffmpeg" }, Directory.GetDirectories(_temp.Combine("tools")).Select(Path.GetFileName));
    }

    // ------------------------------------------------------------------
    // Instalação de ponta a ponta (rede e instalador falsos)
    // ------------------------------------------------------------------

    [Fact]
    public async Task Instalar_Msi_ChamaOMsiexecDoSistemaEApagaODownload()
    {
        bool installed = false;
        var calls = new List<(string Exe, IReadOnlyList<string> Args)>();
        var installer = Installer(new FakeHandler(_ => Ok(Content)),
            run: (exe, args) => { calls.Add((exe, args)); installed = true; return Task.FromResult(3010); },
            installed: _ => installed);
        var progress = new List<ProgressReport>();

        var results = await installer.InstallManyAsync([Package(Content)], new SyncProgress(progress), CancellationToken.None);

        Assert.True(Assert.Single(results).Succeeded);
        var (exe, args) = Assert.Single(calls);
        Assert.Equal(Path.Combine(Environment.SystemDirectory, "msiexec.exe"), exe, ignoreCase: true);
        Assert.Equal(_temp.Combine("tools", "downloads", "teste-1.0.msi"), args[1]);
        Assert.Empty(Directory.GetFiles(_temp.Combine("tools", "downloads")));   // instalador e log apagados
        Assert.Contains(progress, p => p.StatusText.Contains("baixando", StringComparison.Ordinal));
        Assert.Contains(progress, p => p.StatusText.Contains("instalando", StringComparison.Ordinal));
        Assert.True(progress.All(p => p.Percentage < 100));   // o 100% é do chamador
    }

    [Fact]
    public async Task Instalar_JaInstalado_NaoBaixaNada()
    {
        var handler = new FakeHandler(_ => Ok(Content));

        var results = await Installer(handler, installed: _ => true).InstallManyAsync([Package(Content)], null, CancellationToken.None);

        Assert.True(Assert.Single(results).Succeeded);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Instalar_FalhaDeUm_NaoImpedeOsOutros_EOResumoDizQual()
    {
        byte[] other = [1, 2, 3, 4, 5];
        var done = new HashSet<string>();
        var installer = Installer(
            new FakeHandler(r => Ok(r.RequestUri!.AbsolutePath.Contains("bom", StringComparison.Ordinal) ? other : Content)),
            run: (_, args) =>
            {
                bool good = args[1].Contains("bom", StringComparison.Ordinal);
                if (good) done.Add("bom");
                return Task.FromResult(good ? 0 : 1603);
            },
            installed: p => done.Contains(p.Id));

        var results = await installer.InstallManyAsync(
            [Package(Content, id: "ruim", url: "https://example.test/ruim.msi"), Package(other, id: "bom", url: "https://example.test/bom.msi")],
            null, CancellationToken.None);

        Assert.False(results[0].Succeeded);
        Assert.True(results[1].Succeeded);
        Assert.Contains("1603", results[0].Error);
        string summary = ConverterInstaller.Summarize(results);
        Assert.Contains("Instalados: Teste", summary);
        Assert.Contains("Falhou", summary);
    }

    [Fact]
    public async Task Instalar_TerminouMasAFerramentaNaoApareceu_ContaComoFalha()
    {
        var results = await Installer(new FakeHandler(_ => Ok(Content)), installed: _ => false)
            .InstallManyAsync([Package(Content)], null, CancellationToken.None);

        Assert.Contains("não foi encontrado", Assert.Single(results).Error);
    }

    [Fact]
    public async Task Instalar_ZipDoFfmpeg_ExtraiNaPastaTools()
    {
        byte[] zip = File.ReadAllBytes(MakeZip(("ffmpeg-x/bin/ffmpeg.exe", "ffmpeg"), ("ffmpeg-x/LICENSE", "GPL")));
        bool ran = false;
        var installer = Installer(new FakeHandler(_ => Ok(zip)), run: (_, _) => { ran = true; return Task.FromResult(0); },
            installed: _ => File.Exists(_temp.Combine("tools", "ffmpeg", "ffmpeg.exe")));

        var results = await installer.InstallManyAsync(
            [Package(zip, PackageKind.ZipToAppTools, "ffmpeg", url: "https://example.test/ffmpeg.zip")], null, CancellationToken.None);

        Assert.True(Assert.Single(results).Succeeded);
        Assert.False(ran);   // zip não roda instalador nenhum
        Assert.Equal("ffmpeg", File.ReadAllText(_temp.Combine("tools", "ffmpeg", "ffmpeg.exe")));
    }

    [Fact]
    public void Resumo_TodosInstalados()
    {
        var results = new List<ConverterInstallResult>
        {
            new(ConverterPackages.FFmpeg, null), new(ConverterPackages.Pandoc, null), new(ConverterPackages.LibreOffice, null)
        };

        Assert.Equal("Prontos para converter: FFmpeg, Pandoc e LibreOffice.", ConverterInstaller.Summarize(results));
        Assert.Equal("FFmpeg pronto para converter.", ConverterInstaller.Summarize([new(ConverterPackages.FFmpeg, null)]));
    }

    private sealed class SyncProgress(List<ProgressReport> list) : IProgress<ProgressReport>
    {
        public void Report(ProgressReport value) { lock (list) list.Add(value); }
    }
}
