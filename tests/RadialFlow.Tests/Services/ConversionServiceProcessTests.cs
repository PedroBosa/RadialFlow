using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RadialFlow.Services;
using RadialFlow.Tests.TestSupport;
using Xunit;

namespace RadialFlow.Tests;

/// <summary>
/// ConversionService sobre o UnelevatedLauncher. O cmd.exe (System32, protegido) faz o papel das
/// ferramentas externas: escreve no stderr, demora, deixa arquivo parcial.
/// </summary>
public sealed class ConversionServiceProcessTests : IDisposable
{
    private static readonly string Cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    /// <summary>IProgress síncrono (Progress&lt;T&gt; posta no SynchronizationContext e perderia a ordem).</summary>
    private sealed class CollectingProgress : IProgress<ProgressReport>
    {
        public ConcurrentQueue<ProgressReport> Reports { get; } = new();
        public void Report(ProgressReport value) => Reports.Enqueue(value);
    }

    // ------------------------------------------------------------------
    // Progresso do ffmpeg (parser puro)
    // ------------------------------------------------------------------

    [Fact]
    public void TryParseFfmpegProgress_DurationFixaOTotalESemPosicaoNaoHaProgresso()
    {
        TimeSpan total = TimeSpan.Zero;

        bool has = ConversionService.TryParseFfmpegProgress("  Duration: 00:01:40.50, start: 0.000000, bitrate: 128 kb/s",
            ref total, out _, out _);

        Assert.False(has);
        Assert.Equal(TimeSpan.FromSeconds(100.5), total);
    }

    [Fact]
    public void TryParseFfmpegProgress_TimeComTotalConhecido_CalculaOPercentual()
    {
        TimeSpan total = TimeSpan.FromSeconds(200);

        bool has = ConversionService.TryParseFfmpegProgress("size=    512kB time=00:00:50.00 bitrate= 83.9kbits/s speed=25x",
            ref total, out TimeSpan current, out double percentage);

        Assert.True(has);
        Assert.Equal(TimeSpan.FromSeconds(50), current);
        Assert.Equal(25.0, percentage, precision: 6);
    }

    [Fact]
    public void TryParseFfmpegProgress_NuncaPassaDe99ParaOConcluidoSerSoDoFim()
    {
        TimeSpan total = TimeSpan.FromSeconds(10);

        Assert.True(ConversionService.TryParseFfmpegProgress("time=00:00:10.00", ref total, out _, out double percentage));
        Assert.Equal(99.0, percentage);
    }

    [Theory]
    [InlineData("time=00:00:05.00 sem Duration antes")]
    [InlineData("frame=  100 fps=25")]
    [InlineData("")]
    [InlineData("time=abc")]
    public void TryParseFfmpegProgress_LinhaSemTotalOuSemPosicao_NaoGeraProgresso(string line)
    {
        TimeSpan total = TimeSpan.Zero;

        Assert.False(ConversionService.TryParseFfmpegProgress(line, ref total, out _, out double percentage));
        Assert.Equal(0, percentage);
    }

    [Fact]
    public void TryParseFfmpegProgress_DurationSoVale_NaPrimeiraVez()
    {
        TimeSpan total = TimeSpan.Zero;
        ConversionService.TryParseFfmpegProgress("Duration: 00:00:10.00,", ref total, out _, out _);

        // Uma segunda "Duration:" (ex.: a de uma segunda entrada) não troca o total já conhecido.
        ConversionService.TryParseFfmpegProgress("Duration: 00:09:00.00,", ref total, out _, out _);

        Assert.Equal(TimeSpan.FromSeconds(10), total);
    }

    // ------------------------------------------------------------------
    // RunFfmpegWithProgressAsync de ponta a ponta (cmd.exe no lugar do ffmpeg)
    // ------------------------------------------------------------------

    [RequiresUnelevatedLaunchFact]
    public async Task RunFfmpegWithProgressAsync_ConverteLinhasDoStderrEmProgresso()
    {
        var progress = new CollectingProgress();
        string script = "echo   Duration: 00:00:10.00, start: 0.0 1>&2& " +
                        "echo size=0kB time=00:00:05.00 bitrate=0 1>&2& " +
                        "echo size=0kB time=00:00:10.00 bitrate=0 1>&2";

        await ConversionService.RunFfmpegWithProgressAsync(Cmd, ["/c", script], progress, CancellationToken.None);

        double[] percentages = progress.Reports.Select(r => r.Percentage).ToArray();
        Assert.Equal(new[] { 50.0, 99.0, 100.0 }, percentages);
        Assert.Contains("00:00:05 / 00:00:10", progress.Reports.First().StatusText);
        Assert.Contains("concluída", progress.Reports.Last().StatusText);
    }

    [RequiresUnelevatedLaunchFact]
    public async Task RunFfmpegWithProgressAsync_CodigoDiferenteDeZero_FalhaComOFimDoStderr()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ConversionService.RunFfmpegWithProgressAsync(Cmd, ["/c", "echo Invalid data found 1>&2& exit 183"], null, CancellationToken.None));

        Assert.Contains("183", error.Message);
        Assert.Contains("Invalid data found", error.Message);
    }

    // ------------------------------------------------------------------
    // RunToolAsync
    // ------------------------------------------------------------------

    [RequiresUnelevatedLaunchFact]
    public async Task RunToolAsync_DevolveCodigoEOStderr_ESoOStderrEntraNoResumo()
    {
        var lines = new ConcurrentQueue<string>();

        var result = await ConversionService.RunToolAsync(Cmd, ["/c", "echo ruido-stdout& echo falhou feio 1>&2& exit 3"],
            workingDirectory: null, onErrorLine: lines.Enqueue, CancellationToken.None);

        Assert.Equal(3, result.ExitCode);
        Assert.Contains("falhou feio", result.ErrorTail);
        Assert.DoesNotContain("ruido-stdout", result.ErrorTail);
        Assert.Contains(lines, l => l.Contains("falhou feio"));
    }

    [RequiresUnelevatedLaunchFact]
    public async Task RunToolAsync_ResumoDoStderrFicaLimitadoAoFim()
    {
        var result = await ConversionService.RunToolAsync(Cmd,
            ["/c", "for /L %i in (1,1,3000) do @echo erro-numero-%i 1>&2"], null, null, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.True(result.ErrorTail.Length <= 4096, $"O resumo cresceu para {result.ErrorTail.Length} caracteres.");
        Assert.Contains("erro-numero-3000", result.ErrorTail);   // guarda o FIM
        Assert.DoesNotContain("erro-numero-1" + Environment.NewLine, result.ErrorTail);
    }

    // ------------------------------------------------------------------
    // Cancelamento e saída parcial
    // ------------------------------------------------------------------

    [Fact]
    public async Task RunWithPartialOutputCleanupAsync_Falha_ApagaOArquivoParcial()
    {
        string dest = _temp.Combine("saida.part");   // ainda não existe: nasce DURANTE o trabalho

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ConversionService.RunWithPartialOutputCleanupAsync(dest, () =>
            {
                File.WriteAllText(dest, "metade");
                throw new InvalidOperationException("falha no meio");
            }));

        Assert.False(File.Exists(dest));
    }

    [Fact]
    public async Task RunWithPartialOutputCleanupAsync_Sucesso_MantemOArquivo()
    {
        string dest = _temp.Combine("saida.ok");

        await ConversionService.RunWithPartialOutputCleanupAsync(dest, () =>
        {
            File.WriteAllText(dest, "completo");
            return Task.CompletedTask;
        });

        Assert.Equal("completo", File.ReadAllText(dest));
    }

    [Fact]
    public async Task RunWithPartialOutputCleanupAsync_ArquivoQueJaExistia_NuncaEApagado()
    {
        string dest = _temp.CreateFile("existente.txt");
        File.WriteAllText(dest, "do usuario");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ConversionService.RunWithPartialOutputCleanupAsync(dest, () => throw new InvalidOperationException("falha")));

        Assert.Equal("do usuario", File.ReadAllText(dest));
    }

    [RequiresUnelevatedLaunchFact]
    public async Task Cancelar_EncerraAFerramentaEApagaOArquivoParcial()
    {
        // A "ferramenta" grava um arquivo parcial e fica rodando ~30 s. Escrever em caminho relativo
        // (pasta de trabalho = pasta de teste) também prova que o processo SEM elevação consegue
        // gravar numa pasta criada pelo RadialFlow, que é como o LibreOffice usa a pasta temporária.
        string partial = _temp.Combine("saida.part");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        int pid = 0;

        Task run = ConversionService.RunWithPartialOutputCleanupAsync(partial, async () =>
        {
            await UnelevatedLauncher.ExecuteAsync(Cmd, new UnelevatedProcessOptions
            {
                Arguments = ["/c", "echo parcial> saida.part & ping -n 30 127.0.0.1"],
                WorkingDirectory = _temp.Root,
                OnStarted = id => pid = id
            }, cts.Token);
        });

        await WaitUntilAsync(() => File.Exists(partial) && new FileInfo(partial).Length > 0, TimeSpan.FromSeconds(10));
        Assert.False(ProcessTree.IsGone(pid));

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        Assert.True(ProcessTree.IsGone(pid), "A ferramenta continuou viva.");
        Assert.False(File.Exists(partial), "O arquivo parcial ficou para trás.");
    }

    [RequiresUnelevatedLaunchFact]
    public async Task FerramentaTravada_EhEncerradaNoLimite_ComMensagemClara()
    {
        // Encerrar a árvore de processos ao cancelar é garantia do launcher (Cancelar_EncerraAFerramentaEApagaOArquivoParcial).
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var error = await Assert.ThrowsAsync<TimeoutException>(() =>
            ConversionService.RunProcessAsync(Cmd, ["/c", "ping -n 60 127.0.0.1"], null, CancellationToken.None,
                timeout: TimeSpan.FromSeconds(2)));

        Assert.InRange(sw.Elapsed.TotalSeconds, 1.5, 20);
        Assert.Contains("não terminou em 2 segundos", error.Message);
    }

    [RequiresUnelevatedLaunchFact]
    public async Task CanceladoPeloUsuario_ContinuaSendoCancelamento_NaoLimite()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ConversionService.RunProcessAsync(Cmd, ["/c", "ping -n 60 127.0.0.1"], null, cts.Token, timeout: TimeSpan.FromMinutes(5)));
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        DateTime limit = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > limit) throw new TimeoutException("A condição não foi atendida no prazo.");
            await Task.Delay(50);
        }
    }
}
