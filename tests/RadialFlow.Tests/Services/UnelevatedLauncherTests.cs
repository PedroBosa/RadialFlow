using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RadialFlow.Services;
using RadialFlow.Tests.TestSupport;
using Xunit;

namespace RadialFlow.Tests;

/// <summary>
/// Validação feita ANTES de qualquer Process.Start (correção do ProgressWindow → ShowInExplorer).
/// Esta classe NÃO inicia processos: só caminhos que a validação recusa chegam a Start/ShowInExplorer.
/// Os testes que executam processos de verdade (ExecuteAsync) estão em <see cref="UnelevatedLauncherExecuteTests"/>.
/// </summary>
public sealed class UnelevatedLauncherTests : IDisposable
{
    private static readonly string Cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    // ------------------------------------------------------------------
    // ShowInExplorer (botão "Abrir Pasta" da janela de progresso)
    // ------------------------------------------------------------------

    [Fact]
    public void TryBuildExplorerArguments_Arquivo_SelecionaNoExplorer()
    {
        string file = _temp.CreateFile("saida.zip");

        Assert.True(UnelevatedLauncher.TryBuildExplorerArguments(file, out string args));
        Assert.Equal($"/select,\"{file}\"", args);
    }

    [Fact]
    public void TryBuildExplorerArguments_Pasta_AbreSemSeparadorFinal()
    {
        string folder = _temp.CreateDirectory("Extraido");

        Assert.True(UnelevatedLauncher.TryBuildExplorerArguments(folder + "\\", out string args));
        Assert.Equal($"\"{folder}\"", args);
    }

    [Fact]
    public void TryBuildExplorerArguments_RaizDoVolume_NaoEscapaAAspaFinal()
    {
        string root = Path.GetPathRoot(Environment.SystemDirectory)!;   // "C:\"

        Assert.True(UnelevatedLauncher.TryBuildExplorerArguments(root, out string args));
        Assert.Equal($"\"{root}\\\"", args);   // "C:\\" → o parser lê C:\ e fecha a aspa
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"relativo\saida.zip")]
    [InlineData(@"C:\Windows\..\Windows\System32")]
    [InlineData("C:\\Temp\\a\" /select,\"C:\\Windows\\System32\\cmd.exe")]   // injeção de argumento
    [InlineData(@"C:\Temp\saida.zip:ads")]
    [InlineData(@"\\?\C:\Windows")]
    public void TryBuildExplorerArguments_CaminhoInseguro_EhRecusado(string? path)
    {
        Assert.False(UnelevatedLauncher.TryBuildExplorerArguments(path, out string args));
        Assert.Equal("", args);
    }

    [Fact]
    public void TryBuildExplorerArguments_Inexistente_EhRecusado()
    {
        Assert.False(UnelevatedLauncher.TryBuildExplorerArguments(_temp.Combine("nao-existe.zip"), out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(@"..\..\Windows\System32")]
    [InlineData("explorer.exe")]
    public void ShowInExplorer_CaminhoInvalido_DevolveFalseSemAbrirNada(string? path)
    {
        Assert.False(UnelevatedLauncher.ShowInExplorer(path));
    }

    [Fact]
    public void ShowInExplorer_Inexistente_DevolveFalse()
    {
        Assert.False(UnelevatedLauncher.ShowInExplorer(_temp.Combine("sumiu.zip")));
    }

    // ------------------------------------------------------------------
    // TryResolveTarget (favoritos, URLs)
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("https://www.google.com/search?q=a%20b")]
    [InlineData("http://example.com")]
    [InlineData("\"https://example.com/caminho\"")]
    public void TryResolveTarget_UrlHttp_EhAceita(string url)
    {
        Assert.True(UnelevatedLauncher.TryResolveTarget(url, out var target));
        Assert.Equal(UnelevatedLauncher.LaunchKind.Url, target.Kind);
        Assert.DoesNotContain("\"", target.FullPath);
    }

    [Theory]
    [InlineData("file:///C:/Windows/System32/cmd.exe")]
    [InlineData("ftp://example.com/arquivo")]
    [InlineData("javascript://alert(1)")]
    [InlineData("ms-settings://privacy")]
    [InlineData("https://exa\"mple.com/\" --cmd")]
    public void TryResolveTarget_EsquemaOuUrlPerigosa_EhRecusada(string url)
    {
        Assert.False(UnelevatedLauncher.TryResolveTarget(url, out _));
    }

    [Fact]
    public void TryResolveTarget_UrlComQuebraDeLinha_EhRecusada()
    {
        Assert.False(UnelevatedLauncher.TryResolveTarget("https://example.com/\r\ncalc.exe", out _));
        Assert.False(UnelevatedLauncher.IsSafeUrl("https://example.com/\u007F"));
    }

    [Theory]
    [InlineData("cmd.exe")]
    [InlineData("cmd")]          // sem extensão: assume .exe
    [InlineData("CMD.EXE")]
    public void TryResolveTarget_NomeSimples_ResolveSoEmLocalProtegido(string name)
    {
        Assert.True(UnelevatedLauncher.TryResolveTarget(name, out var target));
        Assert.Equal(UnelevatedLauncher.LaunchKind.Executable, target.Kind);
        Assert.Equal(Cmd, target.FullPath, ignoreCase: true);
    }

    [Fact]
    public void TryResolveTarget_ExecutavelPorCaminho_EhClassificado()
    {
        string exe = _temp.CreateFile("app.exe");

        Assert.True(UnelevatedLauncher.TryResolveTarget(exe, out var target));
        Assert.Equal(UnelevatedLauncher.LaunchKind.Executable, target.Kind);
        Assert.Equal(exe, target.FullPath);
    }

    [Fact]
    public void TryResolveTarget_DocumentoEPasta_VaoParaOShell()
    {
        string doc = _temp.CreateFile("leia-me.txt");
        string folder = _temp.CreateDirectory("Pasta");

        Assert.True(UnelevatedLauncher.TryResolveTarget(doc, out var docTarget));
        Assert.Equal(UnelevatedLauncher.LaunchKind.ShellItem, docTarget.Kind);

        Assert.True(UnelevatedLauncher.TryResolveTarget(folder, out var folderTarget));
        Assert.Equal(UnelevatedLauncher.LaunchKind.ShellItem, folderTarget.Kind);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\"\"")]
    [InlineData(@"..\cmd.exe")]
    [InlineData(@"tools\ffmpeg.exe")]
    [InlineData(@"C:\Windows\..\Windows\System32\cmd.exe")]
    [InlineData("javascript:alert(1)")]
    [InlineData("radialflow-nao-existe.exe")]
    public void TryResolveTarget_AlvoInvalido_EhRecusado(string? target)
    {
        Assert.False(UnelevatedLauncher.TryResolveTarget(target, out _));
    }

    [Fact]
    public void Start_AlvoRecusado_DevolveFalseSemLancar()
    {
        Assert.False(UnelevatedLauncher.Start("file:///C:/Windows/System32/cmd.exe"));
        Assert.False(UnelevatedLauncher.Start(@"..\..\Windows\System32\cmd.exe"));
        Assert.False(UnelevatedLauncher.Start(""));
    }

    [Fact]
    public void TryResolveTarget_FavoritosPadrao_ContinuamResolvendo()
    {
        // Regressão: os favoritos de fábrica usam nome simples e precisam passar pela validação nova.
        foreach (var app in RadialFlow.Config.AppSettings.CreateDefault().FavoriteApps)
        {
            Assert.True(UnelevatedLauncher.TryResolveTarget(app.ExecutablePath, out var target), app.ExecutablePath);
            Assert.Equal(UnelevatedLauncher.LaunchKind.Executable, target.Kind);
            Assert.True(Path.IsPathFullyQualified(target.FullPath), target.FullPath);
        }
    }
}

/// <summary>
/// ExecuteAsync com processos REAIS de System32 (cmd, ping, whoami, findstr). Cada teste tem um prazo
/// próprio para nunca travar a suíte. Valem nos dois caminhos do launcher: o teste roda elevado
/// (token do Explorer + CreateProcessWithTokenW) ou comum (CreateProcessW + lista de handles herdados).
/// </summary>
public sealed class UnelevatedLauncherExecuteTests : IDisposable
{
    private static readonly string System32 = Environment.SystemDirectory;
    private static readonly string Cmd = Path.Combine(System32, "cmd.exe");
    private static readonly string Ping = Path.Combine(System32, "ping.exe");
    private static readonly string Whoami = Path.Combine(System32, "whoami.exe");
    private static readonly string Findstr = Path.Combine(System32, "findstr.exe");

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private sealed record RunResult(int ExitCode, IReadOnlyList<string> Output, IReadOnlyList<string> Error);

    private static async Task<RunResult> RunAsync(string exe, string[] args, string? workingDirectory = null, int timeoutSeconds = 30)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        var output = new ConcurrentQueue<string>();
        var error = new ConcurrentQueue<string>();

        int exitCode = await UnelevatedLauncher.ExecuteAsync(exe, new UnelevatedProcessOptions
        {
            Arguments = args,
            WorkingDirectory = workingDirectory,
            OnOutputLine = output.Enqueue,
            OnErrorLine = error.Enqueue
        }, cts.Token);

        return new RunResult(exitCode, output.Select(l => l.Trim()).ToArray(), error.Select(l => l.Trim()).ToArray());
    }

    private static CancellationTokenSource Guard(int seconds = 60) => new(TimeSpan.FromSeconds(seconds));

    // ------------------------------------------------------------------
    // Captura de stdout / stderr e código de saída
    // ------------------------------------------------------------------

    [RequiresUnelevatedLaunchFact]
    public async Task ExecuteAsync_CapturaStdoutLinhaALinha()
    {
        var result = await RunAsync(Cmd, ["/c", "echo linha-1& echo linha-2& echo linha-3"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(new[] { "linha-1", "linha-2", "linha-3" }, result.Output);
        Assert.Empty(result.Error);
    }

    [RequiresUnelevatedLaunchFact]
    public async Task ExecuteAsync_SeparaStdoutDeStderr()
    {
        var result = await RunAsync(Cmd, ["/c", "echo saida& echo erro>&2"]);

        Assert.Equal(new[] { "saida" }, result.Output);
        Assert.Equal(new[] { "erro" }, result.Error);
    }

    [RequiresUnelevatedLaunchTheory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(255)]
    public async Task ExecuteAsync_DevolveOCodigoDeSaida(int code)
    {
        var result = await RunAsync(Cmd, ["/c", $"exit {code}"]);

        Assert.Equal(code, result.ExitCode);
    }

    [RequiresUnelevatedLaunchFact]
    public async Task ExecuteAsync_EntregaAsLinhasEmTempoReal_ComOProcessoAindaRodando()
    {
        using var cts = Guard();
        var firstLine = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<int> run = UnelevatedLauncher.ExecuteAsync(Ping, new UnelevatedProcessOptions
        {
            Arguments = ["-n", "4", "127.0.0.1"],   // ~3 s no total, uma linha por segundo
            OnOutputLine = _ => firstLine.TrySetResult()
        }, cts.Token);

        await firstLine.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(run.IsCompleted);   // a linha chegou ANTES de o processo terminar
        Assert.Equal(0, await run);
    }

    [RequiresUnelevatedLaunchFact]
    public async Task ExecuteAsync_UsaAPastaDeTrabalhoInformada()
    {
        var result = await RunAsync(Cmd, ["/c", "cd"], workingDirectory: _temp.Root);

        Assert.Equal(_temp.Root, Assert.Single(result.Output), ignoreCase: true);
    }

    [RequiresUnelevatedLaunchFact]
    public async Task ExecuteAsync_StdinEhNul_ProcessoQueLeTecladoTerminaNaHora()
    {
        // findstr sem arquivo lê stdin. Com NUL recebe EOF na hora (código 1 = nada encontrado);
        // com o stdin do RadialFlow herdado ficaria esperando até o prazo estourar.
        using var cts = Guard(seconds: 15);

        int exit = await UnelevatedLauncher.ExecuteAsync(Findstr,
            new UnelevatedProcessOptions { Arguments = ["x"] }, cts.Token);

        Assert.Equal(1, exit);
    }

    [RequiresUnelevatedLaunchFact]
    public async Task ExecuteAsync_ArgumentosComEspacoEAspas_ChegamIntactos()
    {
        string file = _temp.CreateFile("com espaco.txt");
        File.WriteAllText(file, "conteudo com espaco");

        // findstr recebe o caminho com espaço como UM argumento (se o quoting falhasse, não acharia o arquivo).
        var result = await RunAsync(Findstr, ["conteudo", file]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(new[] { "conteudo com espaco" }, result.Output);
    }

    // ------------------------------------------------------------------
    // Drenagem: saída maior que o buffer do pipe
    // ------------------------------------------------------------------

    private const string TwentyThousandLines = "for /L %i in (1,1,20000) do @echo line%i";

    [RequiresUnelevatedLaunchFact]
    public async Task ExecuteAsync_SaidaMaiorQueOBufferDoPipe_ChegaCompleta()
    {
        var result = await RunAsync(Cmd, ["/c", TwentyThousandLines]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(20_000, result.Output.Count);
        Assert.Equal("line1", result.Output[0]);
        Assert.Equal("line20000", result.Output[^1]);
    }

    [RequiresUnelevatedLaunchFact]
    public async Task ExecuteAsync_SemCallbacks_AindaDrenaOPipeENaoTrava()
    {
        // Sem ler, ~200 KB encheriam o pipe de 64 KB e o cmd ficaria bloqueado para sempre.
        using var cts = Guard(seconds: 30);

        int exit = await UnelevatedLauncher.ExecuteAsync(Cmd,
            new UnelevatedProcessOptions { Arguments = ["/c", TwentyThousandLines] }, cts.Token);

        Assert.Equal(0, exit);
    }

    [RequiresUnelevatedLaunchFact]
    public async Task ExecuteAsync_CallbackQueLanca_NaoInterrompeALeitura()
    {
        using var cts = Guard(seconds: 30);
        var seen = new ConcurrentQueue<string>();

        int exit = await UnelevatedLauncher.ExecuteAsync(Cmd, new UnelevatedProcessOptions
        {
            Arguments = ["/c", "echo a& echo b& echo c"],
            OnOutputLine = line =>
            {
                seen.Enqueue(line.Trim());
                throw new InvalidOperationException("falha proposital no callback");
            }
        }, cts.Token);

        Assert.Equal(0, exit);
        Assert.Equal(new[] { "a", "b", "c" }, seen);
    }

    [RequiresUnelevatedLaunchFact]
    public async Task ExecuteAsync_OnStarted_RecebeOPidDeUmProcessoVivo()
    {
        using var cts = Guard();
        int pid = 0;
        bool aliveAtStart = false;

        int exit = await UnelevatedLauncher.ExecuteAsync(Cmd, new UnelevatedProcessOptions
        {
            Arguments = ["/c", "exit 0"],
            OnStarted = id =>
            {
                pid = id;
                aliveAtStart = !ProcessTree.IsGone(id);   // criado e ainda suspenso
            }
        }, cts.Token);

        Assert.Equal(0, exit);
        Assert.True(pid > 0);
        Assert.True(aliveAtStart);
    }

    // ------------------------------------------------------------------
    // Nenhum processo roda como administrador
    // ------------------------------------------------------------------

    [RequiresUnelevatedLaunchFact]
    public async Task ExecuteAsync_ProcessoFilho_NaoRodaComIntegridadeAlta()
    {
        // whoami /groups lista o rótulo de integridade pelo SID (independe do idioma do Windows):
        // S-1-16-8192 = Médio, S-1-16-12288 = Alto (elevado), S-1-16-16384 = Sistema.
        var result = await RunAsync(Whoami, ["/groups", "/fo", "csv"]);
        string text = string.Join('\n', result.Output);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("S-1-16-", text);                 // o rótulo foi listado
        Assert.DoesNotContain("S-1-16-12288", text);      // NÃO é administrador elevado
        Assert.DoesNotContain("S-1-16-16384", text);

        if (UnelevatedLauncher.IsElevated())
            Assert.Contains("S-1-16-8192", text);         // o app é elevado e o filho caiu para integridade média
    }

    [RequiresElevationFact]
    public async Task ExecuteAsync_ProcessoFilho_NaoTemPrivilegiosDeAdministrador()
    {
        var result = await RunAsync(Whoami, ["/priv", "/fo", "csv"]);
        string text = string.Join('\n', result.Output);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("SeChangeNotifyPrivilege", text);        // qualquer token tem
        Assert.DoesNotContain("SeDebugPrivilege", text);         // só o token elevado de administrador tem
        Assert.DoesNotContain("SeTakeOwnershipPrivilege", text);
    }

    // ------------------------------------------------------------------
    // Cancelamento
    // ------------------------------------------------------------------

    private sealed record CancelRun(Task<int> Task, CancellationTokenSource Cts, Task FirstLine, Func<int> Pid);

    /// <summary>Inicia um processo longo e devolve quando ele já está imprimindo.</summary>
    private static CancelRun StartLongRunning(string exe, string[] args, Action<string>? onLine = null)
    {
        var cts = Guard();
        var firstLine = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int pid = 0;

        Task<int> task = UnelevatedLauncher.ExecuteAsync(exe, new UnelevatedProcessOptions
        {
            Arguments = args,
            OnStarted = id => pid = id,
            OnOutputLine = line =>
            {
                onLine?.Invoke(line);
                firstLine.TrySetResult();
            }
        }, cts.Token);

        return new CancelRun(task, cts, firstLine.Task, () => pid);
    }

    [RequiresUnelevatedLaunchFact]
    public async Task ExecuteAsync_CancelarEncerraOProcessoERetornaRapido()
    {
        var run = StartLongRunning(Ping, ["-n", "30", "127.0.0.1"]);
        using (run.Cts)
        {
            await run.FirstLine.WaitAsync(TimeSpan.FromSeconds(10));
            int pid = run.Pid();
            Assert.False(ProcessTree.IsGone(pid));   // está rodando (e ainda rodaria ~30 s)

            var stopwatch = Stopwatch.StartNew();
            run.Cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.Task);
            stopwatch.Stop();

            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(4), $"Cancelar demorou {stopwatch.Elapsed}.");
            Assert.True(ProcessTree.IsGone(pid), "O processo continuou vivo depois do cancelamento.");
        }
    }

    [RequiresUnelevatedLaunchFact]
    public async Task ExecuteAsync_CancelarEncerraTambemOsProcessosFilhos()
    {
        // cmd -> ping: o ping é neto do RadialFlow. Só o Job Object (KILL_ON_JOB_CLOSE) alcança o neto.
        var run = StartLongRunning(Cmd, ["/c", "ping -n 30 127.0.0.1"]);
        using (run.Cts)
        {
            await run.FirstLine.WaitAsync(TimeSpan.FromSeconds(10));
            int cmdPid = run.Pid();

            IReadOnlyList<int> grandchildren = ProcessTree.FindChildren(cmdPid, "PING");
            Assert.NotEmpty(grandchildren);   // o neto existe de fato

            run.Cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.Task);

            Assert.True(ProcessTree.IsGone(cmdPid));
            Assert.All(grandchildren, pid => Assert.True(ProcessTree.IsGone(pid), $"O neto {pid} sobreviveu."));
        }
    }

    [Fact]
    public async Task ExecuteAsync_TokenJaCancelado_NaoCriaProcesso()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        bool started = false;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => UnelevatedLauncher.ExecuteAsync(Cmd,
            new UnelevatedProcessOptions { Arguments = ["/c", "exit 0"], OnStarted = _ => started = true }, cts.Token));

        Assert.False(started);
    }

    [RequiresUnelevatedLaunchFact]
    public async Task ExecuteAsync_DepoisDeCancelar_NenhumCallbackChegaMaisTarde()
    {
        int lines = 0;
        var run = StartLongRunning(Ping, ["-n", "30", "127.0.0.1"], onLine: _ => Interlocked.Increment(ref lines));
        using (run.Cts)
        {
            await run.FirstLine.WaitAsync(TimeSpan.FromSeconds(10));
            run.Cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.Task);

            int atReturn = Volatile.Read(ref lines);
            await Task.Delay(1500);   // o ping imprimiria mais linhas se ainda existisse

            Assert.Equal(atReturn, Volatile.Read(ref lines));
        }
    }

    [RequiresUnelevatedLaunchFact]
    public async Task ExecuteAsync_ProcessoQueTerminaSozinho_NaoDeixaProcessoNemLanca()
    {
        using var cts = Guard();
        int pid = 0;

        int exit = await UnelevatedLauncher.ExecuteAsync(Cmd,
            new UnelevatedProcessOptions { Arguments = ["/c", "exit 3"], OnStarted = id => pid = id }, cts.Token);

        Assert.Equal(3, exit);
        Assert.True(ProcessTree.IsGone(pid));
    }

    // ------------------------------------------------------------------
    // Handles: nada vaza, nada é compartilhado entre execuções
    // ------------------------------------------------------------------

    [RequiresUnelevatedLaunchFact]
    public async Task ExecuteAsync_ExecucoesSucessivas_NaoVazamHandles()
    {
        using var self = Process.GetCurrentProcess();

        async Task OneRunAsync(int i)
        {
            if (i % 3 == 0)
            {
                // Terminar cancelando também precisa devolver todos os handles.
                var run = StartLongRunning(Ping, ["-n", "30", "127.0.0.1"]);
                using (run.Cts)
                {
                    await run.FirstLine.WaitAsync(TimeSpan.FromSeconds(10));
                    run.Cts.Cancel();
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.Task);
                }
            }
            else
            {
                await RunAsync(Cmd, ["/c", "echo ok"]);
            }
        }

        for (int i = 1; i <= 12; i++) await OneRunAsync(i);   // aquecimento: pools e caches do .NET se estabilizam
        int before = await SettledHandleCountAsync(self);

        for (int i = 1; i <= 24; i++) await OneRunAsync(i);
        int after = await SettledHandleCountAsync(self);

        // Vazar 1 handle por execução já daria +24. Medido: depois do GC o saldo fica fixo (+4) em lotes seguidos.
        Assert.True(after - before < 12, $"HandleCount subiu de {before} para {after} em 24 execuções.");
    }

    /// <summary>
    /// Contagem de handles depois de GC + finalizadores. Sem isso o número oscila (as threads LongRunning
    /// só devolvem o handle do Thread no finalizador), o que não é vazamento do launcher.
    /// </summary>
    private static async Task<int> SettledHandleCountAsync(Process self)
    {
        for (int i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            await Task.Delay(150);
        }

        self.Refresh();
        return self.HandleCount;
    }

    [RequiresUnelevatedLaunchFact]
    public async Task ExecuteAsync_ProcessoLongoEmAndamento_NaoSeguraOPipeDeOutraExecucao()
    {
        // A (longo) começa primeiro; B (curto) é criado DEPOIS. Se B herdasse o pipe de A (ou A o de B), o EOF
        // de B só chegaria quando A terminasse: a drenagem só liberaria B depois de DrainTimeout (3 s).
        var longRun = StartLongRunning(Ping, ["-n", "12", "127.0.0.1"]);
        using (longRun.Cts)
        {
            await longRun.FirstLine.WaitAsync(TimeSpan.FromSeconds(10));

            var stopwatch = Stopwatch.StartNew();
            var quick = await RunAsync(Cmd, ["/c", "echo rapido"]);
            stopwatch.Stop();

            Assert.Equal(new[] { "rapido" }, quick.Output);
            Assert.False(longRun.Task.IsCompleted);   // A seguia rodando enquanto B terminava
            Assert.True(stopwatch.Elapsed < UnelevatedLauncher.DrainTimeout,
                $"B levou {stopwatch.Elapsed}: o pipe de B ficou preso a outro processo.");

            longRun.Cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => longRun.Task);
        }
    }

    [RequiresUnelevatedLaunchFact]
    public async Task ExecuteAsync_ExecucoesEmParalelo_NaoMisturamAsSaidas()
    {
        var runs = Enumerable.Range(1, 8)
            .Select(id => RunAsync(Cmd, ["/c", $"echo id-{id}& echo err-{id}>&2"]))
            .ToArray();

        RunResult[] results = await Task.WhenAll(runs);

        for (int id = 1; id <= results.Length; id++)
        {
            Assert.Equal(new[] { $"id-{id}" }, results[id - 1].Output);
            Assert.Equal(new[] { $"err-{id}" }, results[id - 1].Error);
        }
    }

    // ------------------------------------------------------------------
    // TrustedPath antes do launch: nada é criado se a validação recusar
    // ------------------------------------------------------------------

    [Fact]
    public async Task ExecuteAsync_ExecutavelEmPastaGravavelPeloUsuario_EhRecusadoSemExecutar()
    {
        // Cópia do cmd.exe em %TEMP% (o usuário pode trocar o arquivo): se rodasse, criaria o marcador.
        string copy = Path.Combine(_temp.Root, "tool.exe");
        File.Copy(Cmd, copy);
        string marker = _temp.Combine("marcador.txt");
        bool started = false;

        var error = await Assert.ThrowsAsync<UnelevatedLaunchException>(() => UnelevatedLauncher.ExecuteAsync(copy,
            new UnelevatedProcessOptions
            {
                Arguments = ["/c", $"echo executou> \"{marker}\""],
                OnStarted = _ => started = true
            }));

        Assert.Equal(UnelevatedLaunchFailure.UntrustedExecutable, error.Failure);
        Assert.False(started);
        Assert.False(File.Exists(marker));
    }

    public static TheoryData<string?> InvalidExecutables => new()
    {
        null,
        "",
        "   ",
        "cmd.exe",                                                    // relativo: dependeria do diretório atual
        @"..\..\Windows\System32\cmd.exe",                            // travessia
        @"C:\Windows\System32\..\System32\cmd.exe",                   // travessia mesmo apontando para um arquivo confiável
        @"C:\Windows\System32\cmd.exe:stream",                        // stream alternativo
        @"C:\Windows\System32\CON",                                   // dispositivo
        @"\\?\C:\Windows\System32\cmd.exe",                           // namespace de dispositivo
        @"C:\Windows\System32\radialflow-nao-existe.exe",             // inexistente
        @"C:\Windows\System32\kernel32.dll",                          // existe e é protegido, mas não é .exe
    };

    [Theory]
    [MemberData(nameof(InvalidExecutables))]
    public async Task ExecuteAsync_ExecutavelInvalido_ELancadoComoNaoConfiavel(string? executable)
    {
        var error = await Assert.ThrowsAsync<UnelevatedLaunchException>(() => UnelevatedLauncher.ExecuteAsync(executable!));

        Assert.Equal(UnelevatedLaunchFailure.UntrustedExecutable, error.Failure);
    }

    [Fact]
    public async Task ExecuteAsync_ArgumentosInvalidos_SaoRecusadosAntesDeExecutar()
    {
        bool started = false;
        var options = new UnelevatedProcessOptions { OnStarted = _ => started = true };

        var withNul = await Assert.ThrowsAsync<UnelevatedLaunchException>(() =>
            UnelevatedLauncher.ExecuteAsync(Cmd, options with { Arguments = ["/c", "echo a\0b"] }));
        var withNull = await Assert.ThrowsAsync<UnelevatedLaunchException>(() =>
            UnelevatedLauncher.ExecuteAsync(Cmd, options with { Arguments = ["/c", null!] }));
        var tooLong = await Assert.ThrowsAsync<UnelevatedLaunchException>(() =>
            UnelevatedLauncher.ExecuteAsync(Cmd, options with { Arguments = ["/c", new string('x', 40_000)] }));
        var relativeDir = await Assert.ThrowsAsync<UnelevatedLaunchException>(() =>
            UnelevatedLauncher.ExecuteAsync(Cmd, options with { WorkingDirectory = "pasta\\relativa" }));
        var missingDir = await Assert.ThrowsAsync<UnelevatedLaunchException>(() =>
            UnelevatedLauncher.ExecuteAsync(Cmd, options with { WorkingDirectory = _temp.Combine("nao-existe") }));

        Assert.All(new[] { withNul, withNull, tooLong, relativeDir, missingDir },
            e => Assert.Equal(UnelevatedLaunchFailure.InvalidArguments, e.Failure));
        Assert.False(started);
    }

    [Fact]
    public async Task ConversionService_RunToolAsync_NaoContornaOTrustedPath()
    {
        string copy = Path.Combine(_temp.Root, "ffmpeg.exe");
        File.Copy(Cmd, copy);

        var error = await Assert.ThrowsAsync<UnelevatedLaunchException>(() =>
            ConversionService.RunToolAsync(copy, ["/c", "exit 0"], workingDirectory: null, onErrorLine: null, CancellationToken.None));

        Assert.Equal(UnelevatedLaunchFailure.UntrustedExecutable, error.Failure);
    }

    // ------------------------------------------------------------------
    // Quoting da linha de comando (conferido com o CommandLineToArgvW do Windows)
    // ------------------------------------------------------------------

    public static TheoryData<string> TrickyArguments => new()
    {
        "", " ", "simples", "com espaço", "tab\taqui", "linha\nnova",
        "aspas\"no\"meio", "\"", "\"\"", "\"entre aspas\"", "--opt=\"x y\"",
        @"C:\pasta\", @"C:\minha pasta\", @"C:\minha pasta\\", @"\\servidor\compartilhamento com espaço\",
        @"barra\antes\"" da aspa", @"duas\\"" barras", @"termina em barra \",
        "ação ç ã 日本語 😀", "&|<>^%!()", "/c", "-i", "[]{}",
    };

    [Theory]
    [MemberData(nameof(TrickyArguments))]
    public void BuildCommandLine_CadaArgumentoVoltaIdenticoPeloParserDoWindows(string argument)
    {
        string commandLine = UnelevatedLauncher.BuildCommandLine(@"C:\Program Files\Tool\tool.exe", ["antes", argument, "depois"]);

        string[] argv = WindowsCommandLine.Parse(commandLine);

        Assert.Equal(new[] { @"C:\Program Files\Tool\tool.exe", "antes", argument, "depois" }, argv);
    }

    [Fact]
    public void BuildCommandLine_ArgumentoGigante_VoltaIdentico()
    {
        string huge = string.Concat(Enumerable.Repeat(@"parte \""com\\ aspas ", 500));

        string[] argv = WindowsCommandLine.Parse(UnelevatedLauncher.BuildCommandLine(@"C:\t.exe", [huge]));

        Assert.Equal(huge, argv[1]);
    }

    [Theory]
    [InlineData("simples", "simples")]
    [InlineData("", "\"\"")]
    [InlineData("a b", "\"a b\"")]
    [InlineData("a\"b", "\"a\\\"b\"")]
    [InlineData(@"C:\dir\", @"C:\dir\")]                       // sem espaço: não precisa de aspas
    [InlineData(@"C:\my dir\", "\"C:\\my dir\\\\\"")]          // barra final dobrada antes da aspa de fechamento
    [InlineData(@"a\""b c", "\"a\\\\\\\"b c\"")]                // barra + aspa: 2n+1 barras
    public void AppendArgument_ProduzOQuotingEsperado(string argument, string expected)
    {
        var builder = new System.Text.StringBuilder();

        UnelevatedLauncher.AppendArgument(builder, argument);

        Assert.Equal(expected, builder.ToString());
    }

    [Fact]
    public void BuildCommandLine_ExecutavelSempreEntreAspas()
    {
        Assert.Equal("\"C:\\Windows\\System32\\cmd.exe\" /c exit",
            UnelevatedLauncher.BuildCommandLine(Cmd.Replace(System32, @"C:\Windows\System32"), ["/c", "exit"]));
    }
}
