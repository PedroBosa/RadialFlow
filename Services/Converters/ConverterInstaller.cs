// ============================================================================
// RadialFlow - Instalação dos conversores
// ----------------------------------------------------------------------------
// Baixa o pacote oficial (ConverterPackages), confere tamanho e SHA-256 e só
// então instala. Cuidados, porque isto roda como ADMINISTRADOR:
//   * o download vai para <app>\tools\downloads, dentro de Arquivos de
//     Programas: um programa comum não consegue trocar o arquivo entre a
//     conferência do hash e a instalação (no %TEMP% conseguiria);
//   * só HTTPS: a URL do pacote e a URL final (depois de redirecionamentos)
//     precisam ser https — um desvio para http é recusado antes de ler o corpo;
//   * o zip do FFmpeg é extraído por nome (só bin\*.exe e a licença), sem
//     caminhos vindos do arquivo: nada sai da pasta de destino;
//   * a etapa de instalação não é cancelada no meio (um MSI interrompido
//     deixaria o programa pela metade); o download pode ser cancelado;
//   * o instalador baixado é apagado no fim; em caso de falha, o log dele
//     fica na pasta de logs do RadialFlow.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using RadialFlow.Infrastructure;
using RadialFlow.Security;

namespace RadialFlow.Services;

/// <summary>Dependências do instalador (os testes trocam rede, pastas e execução).</summary>
internal sealed record ConverterInstallerOptions
{
    /// <summary>Pasta onde o FFmpeg é extraído (<c>&lt;app&gt;\tools</c>).</summary>
    public required string ToolsDirectory { get; init; }

    /// <summary>Pasta dos downloads; precisa ser protegida (só administradores gravam).</summary>
    public required string DownloadsDirectory { get; init; }

    /// <summary>Pasta para guardar o log do instalador quando ele falha.</summary>
    public string? FailureLogDirectory { get; init; }

    public HttpMessageHandler? Handler { get; init; }

    /// <summary>Executa um instalador e devolve o código de saída.</summary>
    public Func<string, IReadOnlyList<string>, Task<int>>? RunInstaller { get; init; }

    /// <summary>true se a ferramenta do pacote já é encontrada.</summary>
    public Func<ConverterPackage, bool>? IsInstalled { get; init; }

    /// <summary>Tempo máximo sem receber nenhum byte antes de desistir do download.</summary>
    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(60);
}

/// <summary>Resultado de um pacote numa instalação de vários.</summary>
internal sealed record ConverterInstallResult(ConverterPackage Package, string? Error)
{
    public bool Succeeded => Error is null;
}

internal sealed class ConverterInstaller(ConverterInstallerOptions options)
{
    /// <summary>Instalador real: pastas dentro da instalação do RadialFlow.</summary>
    public static ConverterInstaller CreateDefault()
    {
        string tools = Path.Combine(AppContext.BaseDirectory, "tools");
        return new ConverterInstaller(new ConverterInstallerOptions
        {
            ToolsDirectory = tools,
            DownloadsDirectory = Path.Combine(tools, "downloads"),
            FailureLogDirectory = AppLog.DirectoryPath
        });
    }

    /// <summary>
    /// Por que não dá para instalar daqui; null se dá. Exige administrador e o RadialFlow numa pasta protegida
    /// (fora dela, o download poderia ser trocado e as ferramentas extraídas seriam ignoradas pelo TrustedPath).
    /// </summary>
    public static string? CheckCanInstall()
    {
        if (!UnelevatedLauncher.IsElevated())
            return Loc.Get("Converters.CannotInstall.NotAdmin");

        string? exe = Environment.ProcessPath;
        if (exe is null || !TrustedPath.IsTrusted(exe))
            return Loc.Get("Converters.CannotInstall.NotInstalled");

        return null;
    }

    /// <summary>
    /// true se a ferramenta do pacote já está disponível para o RadialFlow. Não reseta o cache do
    /// <see cref="ToolResolver"/>: quem verifica um lote (aba Conversores, InstallManyAsync) reseta uma vez antes.
    /// </summary>
    public bool IsInstalled(ConverterPackage package)
    {
        if (options.IsInstalled is { } custom) return custom(package);
        return ToolResolver.FindTool(package.ToolExe) is not null;
    }

    // ------------------------------------------------------------------
    // Vários pacotes (instalador e aba Conversores)
    // ------------------------------------------------------------------

    /// <summary>
    /// Instala os pacotes em sequência; a falha de um não impede os outros. Os que já estão instalados são pulados.
    /// O progresso vai de 0 a 100 no total; o 100% só é informado pelo chamador (que decide o texto final).
    /// </summary>
    public async Task<List<ConverterInstallResult>> InstallManyAsync(IReadOnlyList<ConverterPackage> packages,
        IProgress<ProgressReport>? progress, CancellationToken ct)
    {
        var results = new List<ConverterInstallResult>();
        long total = Math.Max(1, packages.Sum(p => p.SizeBytes));
        long before = 0;

        // Uma varredura por lote: o que cada pacote já tem é conferido contra o mesmo retrato do disco.
        if (options.IsInstalled is null) ToolResolver.ResetCache();

        foreach (ConverterPackage package in packages)
        {
            ct.ThrowIfCancellationRequested();
            double start = before * 99.0 / total, span = package.SizeBytes * 99.0 / total;
            var step = new StepProgress(progress, start, span, packages.Count > 1
                ? Loc.Format("Converters.Progress.StepFormat", package.Name, results.Count + 1, packages.Count)
                : package.Name);

            try
            {
                if (IsInstalled(package))
                {
                    step.Report(1.0, Loc.Get("Converters.Progress.AlreadyInstalled"));
                }
                else
                {
                    await InstallAsync(package, step, ct).ConfigureAwait(false);
                    AppLog.Info("Converters", $"{package.Name} {package.Version} instalado.");
                }

                results.Add(new ConverterInstallResult(package, null));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                AppLog.Error("Converters", $"Falha ao instalar o {package.Name}.", ex);
                results.Add(new ConverterInstallResult(package, ex.Message));
            }

            before += package.SizeBytes;
        }

        return results;
    }

    /// <summary>Resumo para a tela: o que instalou e o que falhou (com o motivo).</summary>
    public static string Summarize(IReadOnlyList<ConverterInstallResult> results)
    {
        var ok = results.Where(r => r.Succeeded).Select(r => r.Package.Name).ToList();
        var failed = results.Where(r => !r.Succeeded).ToList();

        if (failed.Count == 0)
            return ok.Count == 1
                ? Loc.Format("Converters.Summary.OneReady", ok[0])
                : Loc.Format("Converters.Summary.ManyReady", JoinNames(ok));

        string failures = string.Join(" ", failed.Select(f => $"{f.Package.Name}: {f.Error}"));
        return ok.Count == 0
                ? Loc.Format("Converters.Summary.Failed", failures)
                : Loc.Format("Converters.Summary.Partial", JoinNames(ok), failures);
    }

    private static string JoinNames(List<string> names) =>
        names.Count <= 1 ? string.Concat(names) : string.Join(", ", names.Take(names.Count - 1)) + Loc.Get("Common.And") + names[^1];

    // ------------------------------------------------------------------
    // Um pacote
    // ------------------------------------------------------------------

    /// <summary>Baixa, confere e instala. O progresso vai de 0 a 1.</summary>
    internal async Task InstallAsync(ConverterPackage package, StepProgress step, CancellationToken ct)
    {
        Directory.CreateDirectory(options.DownloadsDirectory);
        string download = Path.Combine(options.DownloadsDirectory, package.FileName);
        string log = Path.Combine(options.DownloadsDirectory, $"{package.Id}-install.log");

        try
        {
            double downloadShare = package.Kind == PackageKind.ZipToAppTools ? 0.9 : 0.7;
            await DownloadVerifiedAsync(package, download,
                (received, total) => step.Report(received * downloadShare / total,
                    Loc.Format("Converters.Progress.Downloading",
                        (received / 1048576.0).ToString("0", CultureInfo.CurrentCulture),
                        (total / 1048576.0).ToString("0", CultureInfo.CurrentCulture))), ct).ConfigureAwait(false);

            // Daqui em diante não há cancelamento: interromper um instalador deixaria o programa pela metade.
            step.Report(downloadShare, Loc.Get(package.Kind == PackageKind.ZipToAppTools
                ? "Converters.Progress.Extracting"
                : "Converters.Progress.Installing"));

            if (package.Kind == PackageKind.ZipToAppTools)
            {
                await Task.Run(() => ExtractFfmpeg(download, Path.Combine(options.ToolsDirectory, "ffmpeg")), CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                string exe = package.Kind == PackageKind.Msi ? ResolveMsiexec() : download;
                int code = await (options.RunInstaller ?? RunElevatedAsync)(exe, ConverterPackages.InstallerArguments(package, download, log)).ConfigureAwait(false);
                if (!ConverterPackages.IsSuccessExitCode(code))
                {
                    string? kept = KeepFailureLog(package, log);
                    throw new InvalidOperationException(ConverterPackages.DescribeExitCode(package, code) +
                        (kept is null ? "" : Loc.Format("Converters.Error.LogDetails", kept)));
                }
            }

            // "Instalou agora, então encontra a ferramenta": sem este reset, o cache de "não encontrado" do
            // ToolResolver (30 s) esconderia o executável recém-instalado.
            if (options.IsInstalled is null) ToolResolver.ResetCache();
            if (!IsInstalled(package))
                throw new InvalidOperationException(Loc.Format("Converters.Error.ToolMissing", package.ToolExe));

            step.Report(1.0, Loc.Get("Converters.Progress.Ready"));
        }
        finally
        {
            TryDelete(download);
            TryDelete(download + ".partial");
            TryDelete(log);
        }
    }

    // ------------------------------------------------------------------
    // Download conferido
    // ------------------------------------------------------------------

    /// <summary>
    /// Baixa <paramref name="package"/> para <paramref name="destination"/> conferindo tamanho e SHA-256.
    /// Nada fica no destino se a conferência falhar ou o download for cancelado.
    /// </summary>
    /// <param name="onProgress">(bytes recebidos, total esperado).</param>
    internal async Task DownloadVerifiedAsync(ConverterPackage package, string destination, Action<long, long>? onProgress, CancellationToken ct)
    {
        if (package.Url.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException(Loc.Get("Converters.Error.HttpsOnly"));

        string partial = destination + ".partial";
        TryDelete(partial);

        using var handler = options.Handler is null ? new HttpClientHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 10 } : null;
        using var client = new HttpClient(options.Handler ?? handler!, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"RadialFlow/{AppInfo.Version}");

        bool ok = false;
        try
        {
            using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
            stall.CancelAfter(options.StallTimeout);

            HttpResponseMessage response;
            try
            {
                response = await client.GetAsync(package.Url, HttpCompletionOption.ResponseHeadersRead, stall.Token).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw new InvalidOperationException(Loc.Format("Converters.Error.NoConnection", package.Name), ex);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new InvalidOperationException(Loc.Format("Converters.Error.Timeout", package.Name));
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                    throw new InvalidOperationException(Loc.Format("Converters.Error.DownloadFailed", (int)response.StatusCode));

                // O handler pode ter seguido um 302 para http:// antes de chegar aqui.
                if (response.RequestMessage?.RequestUri is { } finalUri && finalUri.Scheme != Uri.UriSchemeHttps)
                    throw new InvalidOperationException(Loc.Get("Converters.Error.HttpsOnly"));

                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                long limit = package.SizeBytes;
                long received = 0;

                await using (Stream body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
                await using (var file = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
                {
                    var buffer = new byte[1 << 16];
                    while (true)
                    {
                        stall.CancelAfter(options.StallTimeout);
                        int read;
                        try
                        {
                            read = await body.ReadAsync(buffer, stall.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                        {
                            throw new InvalidOperationException(Loc.Get("Converters.Error.DownloadStopped"));
                        }
                        catch (IOException ex)
                        {
                            throw new InvalidOperationException(Loc.Get("Converters.Error.ConnectionLost"), ex);
                        }

                        if (read == 0) break;

                        received += read;
                        if (received > limit)
                            throw new InvalidDataException(Loc.Get("Converters.Error.TooLarge"));

                        hash.AppendData(buffer, 0, read);
                        await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                        onProgress?.Invoke(received, limit);
                    }
                }

                if (received != limit)
                    throw new InvalidDataException(Loc.Get("Converters.Error.Incomplete"));

                string actual = Convert.ToHexStringLower(hash.GetHashAndReset());
                if (!string.Equals(actual, package.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(Loc.Get("Converters.Error.HashMismatch"));
            }

            File.Move(partial, destination, overwrite: true);
            ok = true;
        }
        finally
        {
            if (!ok) TryDelete(partial);
        }
    }

    // ------------------------------------------------------------------
    // FFmpeg: extração do zip
    // ------------------------------------------------------------------

    /// <summary>
    /// Extrai de "ffmpeg-*/bin" só os .exe (ffmpeg, ffprobe, ffplay) e, da raiz do pacote, a licença. Os nomes são
    /// conferidos um a um e o caminho de destino é montado aqui (nunca a partir do zip). Troca a pasta inteira de uma vez.
    /// </summary>
    internal static void ExtractFfmpeg(string zipPath, string targetDirectory)
    {
        string parent = Path.GetDirectoryName(Path.GetFullPath(targetDirectory))!;
        Directory.CreateDirectory(parent);
        string staging = Path.Combine(parent, $".ffmpeg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);

        try
        {
            using (ZipArchive zip = ZipFile.OpenRead(zipPath))
            {
                foreach (ZipArchiveEntry entry in zip.Entries)
                {
                    string[] parts = entry.FullName.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
                    bool binExe = parts.Length == 3 && parts[1].Equals("bin", StringComparison.OrdinalIgnoreCase) &&
                                  parts[2].EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
                    bool license = parts.Length == 2 && parts[1].Equals("LICENSE", StringComparison.OrdinalIgnoreCase);
                    if (!binExe && !license) continue;

                    string name = parts[^1];
                    if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name is "." or ".." || TrustedPath.IsReservedDeviceName(name))
                        continue;

                    entry.ExtractToFile(Path.Combine(staging, binExe ? name : "LICENSE.txt"), overwrite: false);
                }
            }

            if (!File.Exists(Path.Combine(staging, "ffmpeg.exe")))
                throw new InvalidDataException(Loc.Get("Converters.Error.ZipMissingExe"));

            if (Directory.Exists(targetDirectory)) Directory.Delete(targetDirectory, recursive: true);
            Directory.Move(staging, targetDirectory);
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                try { Directory.Delete(staging, recursive: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* sobra temporária */ }
            }
        }
    }

    // ------------------------------------------------------------------
    // Execução do instalador
    // ------------------------------------------------------------------

    private static string ResolveMsiexec() =>
        TrustedPath.TryResolveSystemTool("msiexec.exe", out string path)
            ? path
            : throw new InvalidOperationException(Loc.Get("Converters.Error.MsiexecMissing"));

    /// <summary>
    /// Executa o instalador COM as permissões do RadialFlow (administrador): instalar em Arquivos de Programas exige.
    /// O executável é o msiexec do sistema ou o instalador recém-conferido, numa pasta protegida.
    /// </summary>
    private static async Task<int> RunElevatedAsync(string exe, IReadOnlyList<string> args)
    {
        var info = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(exe)! };
        foreach (string arg in args) info.ArgumentList.Add(arg);

        using Process process = Process.Start(info) ?? throw new InvalidOperationException(Loc.Format("Converters.Error.StartFailed", Path.GetFileName(exe)));
        await process.WaitForExitAsync().ConfigureAwait(false);
        return process.ExitCode;
    }

    private string? KeepFailureLog(ConverterPackage package, string log)
    {
        if (options.FailureLogDirectory is null || !File.Exists(log)) return null;

        try
        {
            Directory.CreateDirectory(options.FailureLogDirectory);
            string kept = Path.Combine(options.FailureLogDirectory, $"instalacao-{package.Id}.log");
            File.Copy(log, kept, overwrite: true);
            return kept;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* em uso: fica para a próxima */ }
    }

    // ------------------------------------------------------------------
    // Progresso de uma etapa dentro do total
    // ------------------------------------------------------------------

    /// <summary>Converte o progresso de um pacote (0 a 1) na faixa dele dentro do total.</summary>
    internal sealed class StepProgress(IProgress<ProgressReport>? target, double start, double span, string label)
    {
        public void Report(double fraction, string text) =>
            target?.Report(new ProgressReport(start + Math.Clamp(fraction, 0, 1) * span, $"{label}: {text}"));
    }
}
