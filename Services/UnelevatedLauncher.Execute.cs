// ============================================================================
// RadialFlow - UnelevatedLauncher.ExecuteAsync
// ----------------------------------------------------------------------------
// Executa uma ferramenta de linha de comando (ffmpeg, magick, pandoc, soffice)
// SEM os privilégios de administrador do RadialFlow, captura stdout/stderr em
// tempo real e permite cancelar de verdade.
//
// Fluxo de ExecuteAsync:
//   1. Valida: caminho canônico, ".exe", TrustedPath.IsTrusted (ninguém comum
//      pode ter trocado o executável), argumentos, pasta de trabalho.
//   2. Cria 2 pipes anônimos (CreatePipe, bInheritHandle = TRUE) e um handle NUL
//      para stdin. O lado de leitura do pai nunca é herdável.
//   3. Cria o processo SUSPENSO:
//        * app elevado  -> token do explorer.exe (não elevado) via
//                          CreateProcessWithTokenW; FALHA FECHADA se não há shell
//                          ou se o único token disponível é elevado;
//        * app comum    -> CreateProcessW com o próprio token, herdando SÓ os 3
//                          handles de E/S (PROC_THREAD_ATTRIBUTE_HANDLE_LIST),
//                          nunca os pipes de outra conversão em andamento.
//   4. Confere que o token do filho NÃO é elevado, associa ao Job Object
//      (KILL_ON_JOB_CLOSE: a árvore inteira morre com o job) e só então retoma.
//   5. Duas threads dedicadas leem os pipes linha a linha (OutputLineReader).
//      A espera pelo término usa RegisterWaitForSingleObject (nenhuma thread presa).
//   6. Cancelamento: TerminateJobObject + TerminateProcess, espera o término e a
//      drenagem (limitados) e lança OperationCanceledException. Quando o método
//      retorna ou lança, o processo e seus filhos JÁ morreram.
//
// Todos os handles são SafeHandle: liberados mesmo se algo falhar no meio.
// ============================================================================

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using RadialFlow.Infrastructure;
using RadialFlow.Security;

namespace RadialFlow.Services;

public static partial class UnelevatedLauncher
{
    /// <summary>Tempo para o fim dos pipes depois que o processo principal sai (netos que seguram o pipe).</summary>
    internal static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(3);

    /// <summary>Tempo máximo esperando o kernel concluir o encerramento forçado.</summary>
    internal static readonly TimeSpan KillTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Limite do Windows é 32.767 caracteres na linha de comando; fica uma folga.</summary>
    private const int MaxCommandLineLength = 32_000;

    private const uint PipeBufferBytes = 64 * 1024;
    private const uint KilledExitCode = 1;

    /// <summary>
    /// Executa <paramref name="executable"/> sem elevação, acompanha a saída e espera o término.
    /// Chamar fora da thread de UI (criar o processo e o token leva dezenas de milissegundos).
    /// </summary>
    /// <param name="executable">Caminho absoluto de um .exe em local protegido (<see cref="TrustedPath.IsTrusted"/>).</param>
    /// <param name="options">Argumentos, pasta de trabalho e callbacks de saída. Nulo = sem argumentos.</param>
    /// <param name="cancellationToken">Ao cancelar, o processo e os filhos são encerrados à força antes de retornar.</param>
    /// <returns>Código de saída do processo (a decisão sobre sucesso/falha é do chamador).</returns>
    /// <exception cref="UnelevatedLaunchException">
    /// Recusado ou não iniciado. Nada do executável rodou: o processo nasce suspenso e só é retomado
    /// depois de todas as verificações.
    /// </exception>
    /// <exception cref="OperationCanceledException">Cancelado; o processo já foi encerrado.</exception>
    public static async Task<int> ExecuteAsync(
        string executable,
        UnelevatedProcessOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new UnelevatedProcessOptions();
        cancellationToken.ThrowIfCancellationRequested();

        // 1) Validação: TUDO acontece antes de qualquer processo existir.
        string exe = ValidateExecutable(executable);
        string? workingDirectory = ValidateWorkingDirectory(options.WorkingDirectory);
        string commandLine = BuildCommandLine(exe, options.Arguments);

        // 2-4) Processo suspenso, não elevado, dentro do job.
        using ChildProcess child = StartChild(exe, commandLine, workingDirectory);
        using var exitHandle = new ProcessWaitHandle(child.Handle);

        var gate = new ReaderGate();
        var exitSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        RegisteredWaitHandle? registration = null;

        try
        {
            // 5) Leitores primeiro: o pipe esvazia desde o primeiro byte.
            Task pumps = Task.WhenAll(
                StartPump(child.StdOut, options.OnOutputLine, gate),
                StartPump(child.StdErr, options.OnErrorLine, gate));

            registration = ThreadPool.RegisterWaitForSingleObject(
                exitHandle,
                static (state, _) => ((TaskCompletionSource)state!).TrySetResult(),
                exitSignal,
                Timeout.Infinite,
                executeOnlyOnce: true);

            NotifyStarted(options.OnStarted, child.ProcessId);
            child.Resume();

            try
            {
                await exitSignal.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 6) Cancelamento: mata a árvore e só devolve o controle com tudo encerrado.
                child.Terminate();
                await TryWaitAsync(exitSignal.Task, KillTimeout).ConfigureAwait(false);
                await TryWaitAsync(pumps, KillTimeout).ConfigureAwait(false);
                throw;
            }

            int exitCode = child.GetExitCode();

            // Saída normal: o resto da saída ainda está no pipe. Se um neto segurar o pipe, não esperamos para sempre.
            if (!await TryWaitAsync(pumps, DrainTimeout).ConfigureAwait(false))
            {
                child.Terminate();
                await TryWaitAsync(pumps, KillTimeout).ConfigureAwait(false);
            }

            return exitCode;
        }
        finally
        {
            child.Terminate();   // netos esquecidos e qualquer caminho de erro
            gate.Close();
            registration?.Unregister(null);
        }
    }

    // ------------------------------------------------------------------
    // Validação (internal para os testes)
    // ------------------------------------------------------------------

    /// <summary>Caminho canônico de um .exe existente em local protegido, ou <see cref="UnelevatedLaunchException"/>.</summary>
    private static string ValidateExecutable(string? executable)
    {
        if (TrustedPath.TryGetSafeFullPath(executable, out string full) &&
            full.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
            TrustedPath.IsTrusted(full))
            return full;

        throw new UnelevatedLaunchException(UnelevatedLaunchFailure.UntrustedExecutable,
            $"'{Path.GetFileName(executable)}' não existe ou não está em um local protegido e não será executado.");
    }

    private static string? ValidateWorkingDirectory(string? workingDirectory)
    {
        if (workingDirectory is null) return null;

        if (TrustedPath.TryGetSafeFullPath(workingDirectory, out string full) && Directory.Exists(full))
            return full;

        throw new UnelevatedLaunchException(UnelevatedLaunchFailure.InvalidArguments,
            $"A pasta de trabalho '{workingDirectory}' é inválida ou não existe.");
    }

    /// <summary>
    /// Monta a linha de comando com as regras de CommandLineToArgvW / MSVCRT: o processo recebe exatamente
    /// os argumentos informados (espaços, aspas e barras invertidas incluídos), sem passar por cmd.exe.
    /// </summary>
    internal static string BuildCommandLine(string executable, IEnumerable<string>? arguments)
    {
        var builder = new StringBuilder();
        builder.Append('"').Append(executable).Append('"');   // o caminho não tem aspas (TrustedPath) e termina em ".exe"

        if (arguments is not null)
        {
            foreach (string? argument in arguments)
            {
                if (argument is null)
                    throw new UnelevatedLaunchException(UnelevatedLaunchFailure.InvalidArguments, "Argumento nulo.");
                if (argument.Contains('\0'))
                    throw new UnelevatedLaunchException(UnelevatedLaunchFailure.InvalidArguments,
                        "Argumento com caractere NUL.");

                builder.Append(' ');
                AppendArgument(builder, argument);

                if (builder.Length > MaxCommandLineLength)
                    throw new UnelevatedLaunchException(UnelevatedLaunchFailure.InvalidArguments,
                        "A linha de comando é longa demais.");
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Acrescenta um argumento com o quoting do Windows: aspas só quando precisa; <c>"</c> vira <c>\"</c>;
    /// barras invertidas só são dobradas quando antecedem uma aspa (ou o fechamento).
    /// </summary>
    internal static void AppendArgument(StringBuilder builder, string argument)
    {
        if (argument.Length > 0 && argument.AsSpan().IndexOfAny(" \t\n\v\"") < 0)
        {
            builder.Append(argument);
            return;
        }

        builder.Append('"');
        int backslashes = 0;
        foreach (char c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            if (c == '"')
                builder.Append('\\', backslashes * 2 + 1).Append('"');   // barras + aspa escapada
            else
                builder.Append('\\', backslashes).Append(c);

            backslashes = 0;
        }

        builder.Append('\\', backslashes * 2).Append('"');   // barras finais não podem escapar a aspa de fechamento
    }

    // ------------------------------------------------------------------
    // Criação do processo
    // ------------------------------------------------------------------

    /// <summary>
    /// Cria o processo SUSPENSO, não elevado e dentro do job. Se qualquer passo falhar, o processo
    /// criado é encerrado e todos os handles são liberados antes da exceção sair.
    /// </summary>
    private static ChildProcess StartChild(string exe, string commandLine, string? workingDirectory)
    {
        bool elevatedHost = IsElevated();

        SafeKernelHandle? shellToken = null;
        SafeFileHandle? outRead = null, outWrite = null, errRead = null, errWrite = null, nul = null;
        SafeKernelHandle? job = null, process = null, thread = null;
        bool handedOver = false;

        try
        {
            if (elevatedHost)
            {
                // Nunca existe um plano B elevado: sem token do usuário, não executa.
                shellToken = OpenShellPrimaryToken(TOKEN_ALL_ACCESS)
                    ?? throw new UnelevatedLaunchException(UnelevatedLaunchFailure.ShellUnavailable,
                        "O RadialFlow é administrador e não encontrou o Explorer (shell do usuário) para iniciar a " +
                        "ferramenta sem privilégios de administrador. Nada foi executado.");

                if (IsTokenElevated(shellToken) != false)
                    throw new UnelevatedLaunchException(UnelevatedLaunchFailure.ElevatedToken,
                        "A sessão do usuário só tem token de administrador (UAC desligado?). " +
                        "A ferramenta não será executada como administrador.");
            }

            var security = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(), bInheritHandle = true };
            (outRead, outWrite) = CreateInheritablePipe(ref security);
            (errRead, errWrite) = CreateInheritablePipe(ref security);

            // stdin = NUL: a ferramenta nunca fica esperando teclado nem herda o stdin do RadialFlow.
            nul = CreateFileW("NUL", GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, ref security, OPEN_EXISTING, 0, IntPtr.Zero);
            if (nul.IsInvalid) throw Failure("Falha ao abrir NUL para stdin");

            job = CreateKillOnCloseJob();

            PROCESS_INFORMATION info = elevatedHost
                ? CreateWithShellToken(shellToken!, exe, commandLine, workingDirectory, nul, outWrite, errWrite)
                : CreateWithCurrentToken(exe, commandLine, workingDirectory, nul, outWrite, errWrite);

            process = new SafeKernelHandle(info.hProcess, ownsHandle: true);
            thread = new SafeKernelHandle(info.hThread, ownsHandle: true);

            try
            {
                // As cópias do pai das pontas do filho precisam fechar, senão o EOF nunca chega.
                outWrite.Dispose();
                errWrite.Dispose();
                nul.Dispose();

                // Prova no resultado, não só na intenção: o token DO PROCESSO CRIADO não é elevado.
                if (elevatedHost && IsProcessElevated(process) == true)
                    throw new UnelevatedLaunchException(UnelevatedLaunchFailure.ElevatedToken,
                        "O processo foi criado com token elevado e foi encerrado antes de executar qualquer código.");

                if (job is not null && !AssignProcessToJobObject(job, process))
                {
                    // Sem job a árvore não morre junto; ainda dá para encerrar o processo principal.
                    AppLog.Warning("Launcher", $"AssignProcessToJobObject falhou (erro {Marshal.GetLastWin32Error()}); " +
                                               "o cancelamento encerrará só o processo principal.");
                    job.Dispose();
                    job = null;
                }

                var child = new ChildProcess(process, thread, job, (int)info.dwProcessId, outRead, errRead);
                handedOver = true;
                return child;
            }
            catch
            {
                TerminateProcess(process, KilledExitCode);   // ainda suspenso: nenhuma instrução dele rodou
                throw;
            }
        }
        finally
        {
            shellToken?.Dispose();

            if (!handedOver)
            {
                outRead?.Dispose();
                outWrite?.Dispose();
                errRead?.Dispose();
                errWrite?.Dispose();
                nul?.Dispose();
                job?.Dispose();
                thread?.Dispose();
                process?.Dispose();
            }
        }
    }

    /// <summary>App elevado: o filho recebe o token (primário, não elevado) do shell do usuário.</summary>
    private static PROCESS_INFORMATION CreateWithShellToken(SafeKernelHandle token, string exe, string commandLine,
        string? workingDirectory, SafeFileHandle stdin, SafeFileHandle stdout, SafeFileHandle stderr)
    {
        var startup = new STARTUPINFO
        {
            cb = Marshal.SizeOf<STARTUPINFO>(),
            lpDesktop = @"winsta0\default",
            dwFlags = STARTF_USESTDHANDLES | STARTF_USESHOWWINDOW,
            wShowWindow = SW_HIDE,
            hStdInput = stdin.DangerousGetHandle(),
            hStdOutput = stdout.DangerousGetHandle(),
            hStdError = stderr.DangerousGetHandle()
        };

        var cmd = new StringBuilder(commandLine);   // a API pode modificar o buffer

        bool created = CreateProcessWithTokenW(token, LOGON_WITH_PROFILE, exe, cmd,
            CREATE_SUSPENDED | CREATE_NO_WINDOW, IntPtr.Zero, workingDirectory, ref startup, out PROCESS_INFORMATION info);
        int error = Marshal.GetLastWin32Error();

        GC.KeepAlive(stdin);
        GC.KeepAlive(stdout);
        GC.KeepAlive(stderr);

        if (!created) throw Failure("CreateProcessWithTokenW falhou", error);
        return info;
    }

    /// <summary>App comum: o próprio token; o filho herda SOMENTE os três handles de E/S.</summary>
    private static unsafe PROCESS_INFORMATION CreateWithCurrentToken(string exe, string commandLine,
        string? workingDirectory, SafeFileHandle stdin, SafeFileHandle stdout, SafeFileHandle stderr)
    {
        IntPtr[] inherited = [stdin.DangerousGetHandle(), stdout.DangerousGetHandle(), stderr.DangerousGetHandle()];

        // Sem a lista, bInheritHandles = TRUE herdaria QUALQUER handle herdável do processo, inclusive os
        // pipes de outra conversão em andamento (e o EOF dela nunca chegaria).
        nuint size = 0;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);   // falha de propósito: devolve o tamanho
        IntPtr attributeList = Marshal.AllocHGlobal((nint)size);

        try
        {
            if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref size))
                throw Failure("InitializeProcThreadAttributeList falhou");

            try
            {
                fixed (IntPtr* handles = inherited)
                {
                    if (!UpdateProcThreadAttribute(attributeList, 0, PROC_THREAD_ATTRIBUTE_HANDLE_LIST,
                            (IntPtr)handles, (nuint)(inherited.Length * IntPtr.Size), IntPtr.Zero, IntPtr.Zero))
                        throw Failure("UpdateProcThreadAttribute falhou");

                    var startup = new STARTUPINFOEX
                    {
                        StartupInfo = new STARTUPINFO
                        {
                            cb = Marshal.SizeOf<STARTUPINFOEX>(),
                            dwFlags = STARTF_USESTDHANDLES | STARTF_USESHOWWINDOW,
                            wShowWindow = SW_HIDE,
                            hStdInput = inherited[0],
                            hStdOutput = inherited[1],
                            hStdError = inherited[2]
                        },
                        lpAttributeList = attributeList
                    };

                    var cmd = new StringBuilder(commandLine);

                    bool created = CreateProcessW(exe, cmd, IntPtr.Zero, IntPtr.Zero, inheritHandles: true,
                        CREATE_SUSPENDED | CREATE_NO_WINDOW | EXTENDED_STARTUPINFO_PRESENT,
                        IntPtr.Zero, workingDirectory, ref startup, out PROCESS_INFORMATION info);
                    int error = Marshal.GetLastWin32Error();

                    GC.KeepAlive(stdin);
                    GC.KeepAlive(stdout);
                    GC.KeepAlive(stderr);

                    if (!created) throw Failure("CreateProcessW falhou", error);
                    return info;
                }
            }
            finally
            {
                DeleteProcThreadAttributeList(attributeList);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(attributeList);
        }
    }

    /// <summary>Pipe anônimo (CreatePipe) com as duas pontas herdáveis; a ponta de LEITURA do pai deixa de ser herdável.</summary>
    private static (SafeFileHandle Read, SafeFileHandle Write) CreateInheritablePipe(ref SECURITY_ATTRIBUTES security)
    {
        if (!CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref security, PipeBufferBytes))
            throw Failure("CreatePipe falhou");

        if (!SetHandleInformation(read, HANDLE_FLAG_INHERIT, 0))
        {
            UnelevatedLaunchException error = Failure("SetHandleInformation falhou");
            read.Dispose();
            write.Dispose();
            throw error;
        }

        return (read, write);
    }

    /// <summary>Job que mata todos os processos dele quando o último handle fecha; null se o Windows recusar.</summary>
    private static SafeKernelHandle? CreateKillOnCloseJob()
    {
        SafeKernelHandle job = CreateJobObjectW(IntPtr.Zero, null);
        if (job.IsInvalid)
        {
            AppLog.Warning("Launcher", $"CreateJobObject falhou (erro {Marshal.GetLastWin32Error()}).");
            return null;
        }

        var limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;

        int size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(limits, buffer, fDeleteOld: false);
            if (SetInformationJobObject(job, JobObjectExtendedLimitInformation, buffer, (uint)size))
                return job;

            AppLog.Warning("Launcher", $"SetInformationJobObject falhou (erro {Marshal.GetLastWin32Error()}).");
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        job.Dispose();
        return null;
    }

    /// <summary>true/false = token do processo elevado ou não; null = não foi possível consultar (apenas registrado).</summary>
    private static bool? IsProcessElevated(SafeKernelHandle process)
    {
        if (!OpenProcessToken(process, TOKEN_QUERY, out SafeKernelHandle token))
        {
            AppLog.Warning("Launcher", $"Não foi possível abrir o token do filho (erro {Marshal.GetLastWin32Error()}).");
            return null;
        }

        using (token)
            return IsTokenElevated(token);
    }

    private static UnelevatedLaunchException Failure(string what) => Failure(what, Marshal.GetLastWin32Error());

    private static UnelevatedLaunchException Failure(string what, int error) =>
        new(UnelevatedLaunchFailure.LaunchFailed, $"{what} (erro {error}).", new Win32Exception(error));

    // ------------------------------------------------------------------
    // Leitura, espera e utilitários
    // ------------------------------------------------------------------

    /// <summary>Lê o pipe numa thread dedicada até o EOF; a própria thread fecha o stream (e o handle).</summary>
    private static Task StartPump(SafeFileHandle readHandle, Action<string>? onLine, ReaderGate gate)
    {
        // bufferSize 0: sem buffer duplo (o OutputLineReader já lê em blocos).
        var stream = new FileStream(readHandle, FileAccess.Read, bufferSize: 0, isAsync: false);

        return Task.Factory.StartNew(() =>
        {
            try
            {
                using (stream)
                {
                    // Sem callback, ainda assim lê: pipe cheio bloquearia o processo.
                    OutputLineReader.Pump(stream, line =>
                    {
                        if (onLine is not null && !gate.IsClosed) onLine(line);
                    });
                }
            }
            catch (Exception ex)   // IOException (pipe quebrado), ObjectDisposedException (handle fechado após timeout)
            {
                AppLog.Warning("Launcher", $"Leitura do pipe terminou com erro: {ex.Message}");
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    private static void NotifyStarted(Action<int>? onStarted, int processId)
    {
        try
        {
            onStarted?.Invoke(processId);
        }
        catch (Exception ex)
        {
            AppLog.Warning("Launcher", $"OnStarted falhou: {ex.Message}");
        }
    }

    /// <summary>true se a tarefa terminou no prazo; false se estourou. Não propaga o timeout.</summary>
    private static async Task<bool> TryWaitAsync(Task task, TimeSpan timeout)
    {
        try
        {
            await task.WaitAsync(timeout).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    /// <summary>Impede novas chamadas de callback depois que ExecuteAsync terminou.</summary>
    private sealed class ReaderGate
    {
        private volatile bool _closed;
        public bool IsClosed => _closed;
        public void Close() => _closed = true;
    }

    /// <summary>WaitHandle sobre o handle do processo, SEM ser dono dele (quem fecha é o ChildProcess).</summary>
    private sealed class ProcessWaitHandle : WaitHandle
    {
        public ProcessWaitHandle(SafeKernelHandle process) =>
            SafeWaitHandle = new SafeWaitHandle(process.DangerousGetHandle(), ownsHandle: false);
    }

    /// <summary>Processo criado: handles do processo/thread/job e pontas de leitura dos pipes.</summary>
    private sealed class ChildProcess : IDisposable
    {
        private readonly SafeKernelHandle _thread;
        private readonly SafeKernelHandle? _job;

        public ChildProcess(SafeKernelHandle process, SafeKernelHandle thread, SafeKernelHandle? job,
            int processId, SafeFileHandle stdOut, SafeFileHandle stdErr)
        {
            Handle = process;
            _thread = thread;
            _job = job;
            ProcessId = processId;
            StdOut = stdOut;
            StdErr = stdErr;
        }

        public SafeKernelHandle Handle { get; }
        public int ProcessId { get; }
        public SafeFileHandle StdOut { get; }
        public SafeFileHandle StdErr { get; }

        /// <summary>Retoma a thread principal (o processo nasceu suspenso) e solta o handle da thread.</summary>
        public void Resume()
        {
            if (ResumeThread(_thread) == uint.MaxValue)
                throw Failure("ResumeThread falhou");

            _thread.Dispose();
        }

        public int GetExitCode() =>
            GetExitCodeProcess(Handle, out uint code)
                ? unchecked((int)code)
                : throw Failure("GetExitCodeProcess falhou");

        /// <summary>Encerra a árvore (job) e o processo principal. Idempotente; nunca lança.</summary>
        public void Terminate()
        {
            try
            {
                if (_job is { IsClosed: false }) TerminateJobObject(_job, KilledExitCode);
                if (!Handle.IsClosed) TerminateProcess(Handle, KilledExitCode);   // falha sem efeito se já saiu
            }
            catch (ObjectDisposedException)
            {
                // já liberado
            }
        }

        /// <summary>Fechar o job primeiro: KILL_ON_JOB_CLOSE derruba qualquer processo que tenha sobrado.</summary>
        public void Dispose()
        {
            _job?.Dispose();
            Handle.Dispose();
            _thread.Dispose();
            StdOut.Dispose();
            StdErr.Dispose();
        }
    }
}
