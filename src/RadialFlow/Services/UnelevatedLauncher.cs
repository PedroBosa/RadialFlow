// ============================================================================
// RadialFlow - Lançar programas SEM herdar a elevação do RadialFlow
// ----------------------------------------------------------------------------
// O app roda como administrador. Tudo que ele abre (navegador, apps favoritos,
// pastas no Explorer) herdaria esse token. Aqui o processo é criado com o token
// do SHELL do usuário (explorer.exe), isto é, não elevado, que é o que o usuário
// espera ao clicar num favorito, e impede que um settings.json adulterado vire
// escalada.
//
// Técnica: OpenProcess(shell) -> OpenProcessToken -> DuplicateTokenEx ->
// CreateProcessWithTokenW.
//
// Dois jeitos de usar:
//   * Start / ShowInExplorer        abrem e esquecem (URLs, favoritos, pasta no Explorer);
//   * ExecuteAsync                  executa e ACOMPANHA (ferramentas de conversão): captura
//                                   stdout/stderr linha a linha, devolve o código de saída e
//                                   cancela de verdade (ver UnelevatedLauncher.Execute.cs).
// A camada Win32 (P/Invoke, SafeHandles) fica em UnelevatedLauncher.Native.cs.
//
// Todo alvo passa por Security/TrustedPath ANTES de qualquer Process.Start:
//   * URL: só http/https, sem aspas nem caracteres de controle;
//   * nome simples ("notepad.exe"): só System32, Windows ou PATH protegido;
//   * caminho: absoluto e canônico (sem "..", ADS, nomes reservados...);
//   * o explorer.exe usado como intermediário também é verificado.
// ============================================================================

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using RadialFlow.Infrastructure;
using RadialFlow.Security;

namespace RadialFlow.Services;

public static partial class UnelevatedLauncher
{
    /// <summary>Como o alvo validado deve ser aberto.</summary>
    internal enum LaunchKind
    {
        /// <summary>URL http/https, aberta pela associação padrão (navegador).</summary>
        Url,

        /// <summary>Executável (.exe/.com/.bat/.cmd), iniciado direto com os argumentos.</summary>
        Executable,

        /// <summary>Documento, atalho ou pasta, aberto pela associação padrão do shell.</summary>
        ShellItem
    }

    /// <summary>Alvo já validado por <see cref="TrustedPath"/>; <see cref="FullPath"/> é canônico (ou a URL).</summary>
    internal readonly record struct LaunchTarget(LaunchKind Kind, string FullPath);

    private static readonly char[] PathMarkers = ['\\', '/', ':'];

    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>
    /// true se esta sessão consegue abrir um processo sem elevação. Sessão elevada sem o token do
    /// shell (UAC desligado, como em runners de CI) não tem como: o RadialFlow recusa rodar como administrador.
    /// </summary>
    internal static bool CanLaunchUnelevated()
    {
        if (!IsElevated()) return true;

        using SafeKernelHandle? token = OpenShellPrimaryToken(TOKEN_ALL_ACCESS);
        return token is not null && IsTokenElevated(token) == false;
    }

    /// <summary>
    /// Abre um executável, documento, atalho, pasta ou URL com os privilégios do usuário comum.
    /// </summary>
    /// <param name="target">URL http/https, nome simples de ferramenta do sistema ou caminho absoluto.</param>
    /// <param name="arguments">Argumentos (só usados para executáveis).</param>
    /// <returns>false se o alvo for rejeitado pela validação ou se não for possível abrir sem elevação
    /// (nada é aberto elevado). Nunca lança exceção.</returns>
    public static bool Start(string target, string arguments = "")
    {
        try
        {
            if (!TryResolveTarget(target, out LaunchTarget launch))
            {
                AppLog.Warning("Launcher", $"Alvo rejeitado pela validação de caminho: '{target}'.");
                return false;
            }

            string args = arguments?.Trim() ?? "";
            return launch.Kind == LaunchKind.Executable
                ? LaunchExecutable(launch.FullPath, args)
                : LaunchViaShell(launch.FullPath);
        }
        catch (Exception ex)
        {
            AppLog.Error("Launcher", $"Falha ao abrir '{target}'", ex);
            return false;
        }
    }

    /// <summary>
    /// Mostra um arquivo (selecionado) ou abre uma pasta no Explorer, sem elevação.
    /// O caminho é validado por <see cref="TrustedPath.TryGetSafeFullPath"/> e precisa existir.
    /// </summary>
    /// <returns>false se o caminho for rejeitado, não existir ou o Explorer não puder ser aberto.
    /// Nunca lança exceção.</returns>
    public static bool ShowInExplorer(string? path)
    {
        try
        {
            if (!TryBuildExplorerArguments(path, out string arguments))
            {
                AppLog.Warning("Launcher", $"Caminho rejeitado para o Explorer: '{path}'.");
                return false;
            }

            if (!TrustedPath.TryResolveSystemTool("explorer.exe", out string explorer))
            {
                AppLog.Warning("Launcher", "explorer.exe não encontrado em local protegido.");
                return false;
            }

            if (!IsElevated())
            {
                using Process? _ = Process.Start(new ProcessStartInfo(explorer, arguments) { UseShellExecute = false });
                return true;
            }

            return StartWithShellToken($"\"{explorer}\" {arguments}", null) ||
                   StartExplorerHandoff(explorer, arguments);
        }
        catch (Exception ex)
        {
            AppLog.Error("Launcher", $"Falha ao abrir '{path}' no Explorer", ex);
            return false;
        }
    }

    // ------------------------------------------------------------------
    // Validação (internal para os testes)
    // ------------------------------------------------------------------

    /// <summary>
    /// Classifica e valida o alvo. Nome simples só é resolvido em locais protegidos
    /// (<see cref="TrustedPath.TryResolveSystemTool"/>), nunca no diretório atual.
    /// </summary>
    internal static bool TryResolveTarget(string? target, out LaunchTarget launch)
    {
        launch = default;
        if (string.IsNullOrWhiteSpace(target)) return false;

        string value = target.Trim();
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"') value = value[1..^1].Trim();
        if (value.Length == 0) return false;

        if (value.Contains("://", StringComparison.Ordinal))
        {
            if (!IsSafeUrl(value)) return false;
            launch = new LaunchTarget(LaunchKind.Url, value);
            return true;
        }

        // "notepad.exe", "calc": System32 -> Windows -> PATH protegido.
        if (value.IndexOfAny(PathMarkers) < 0)
        {
            string name = Path.HasExtension(value) ? value : value + ".exe";
            if (!TrustedPath.TryResolveSystemTool(name, out string resolved)) return false;
            launch = new LaunchTarget(LaunchKind.Executable, resolved);
            return true;
        }

        if (!TrustedPath.TryGetSafeFullPath(value, out string full)) return false;

        if (Directory.Exists(full))
        {
            launch = new LaunchTarget(LaunchKind.ShellItem, full);
            return true;
        }

        if (!File.Exists(full)) return false;

        launch = new LaunchTarget(IsDirectlyExecutable(full) ? LaunchKind.Executable : LaunchKind.ShellItem, full);
        return true;
    }

    /// <summary>Somente http/https, sem aspas nem caracteres de controle (a URL vai entre aspas na linha de comando).</summary>
    internal static bool IsSafeUrl(string value)
    {
        foreach (char c in value)
            if (c < 0x20 || c == 0x7F || c == '"') return false;

        return Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) &&
               (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);
    }

    /// <summary>
    /// Monta os argumentos do Explorer: <c>/select,"arquivo"</c> ou <c>"pasta"</c>.
    /// Aspas são impossíveis no caminho (rejeitadas pela validação), logo não há como escapar delas.
    /// </summary>
    internal static bool TryBuildExplorerArguments(string? path, out string arguments)
    {
        arguments = "";
        if (!TrustedPath.TryGetSafeFullPath(path, out string full)) return false;

        if (File.Exists(full))
        {
            arguments = $"/select,\"{full}\"";
            return true;
        }

        if (Directory.Exists(full))
        {
            full = Path.TrimEndingDirectorySeparator(full);
            // Raiz ("C:\"): \" seria lido como aspas escapadas; "C:\\" vira C:\ para o parser.
            if (full.EndsWith('\\')) full += "\\";
            arguments = $"\"{full}\"";
            return true;
        }

        return false;
    }

    private static bool IsDirectlyExecutable(string path)
    {
        string ext = Path.GetExtension(path);
        return ext.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".com", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".bat", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".cmd", StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------
    // Lançamento
    // ------------------------------------------------------------------

    private static bool LaunchExecutable(string exe, string arguments)
    {
        string? workingDir = Path.GetDirectoryName(exe);

        if (!IsElevated())
        {
            using Process? _ = Process.Start(new ProcessStartInfo(exe, arguments)
            {
                UseShellExecute = true,
                WorkingDirectory = workingDir ?? ""
            });
            return true;
        }

        string commandLine = arguments.Length == 0 ? $"\"{exe}\"" : $"\"{exe}\" {arguments}";
        if (StartWithShellToken(commandLine, workingDir)) return true;

        // Sem argumentos, o explorer.exe repassa a abertura ao shell já em execução (não elevado).
        if (arguments.Length == 0 && TrustedPath.TryResolveSystemTool("explorer.exe", out string explorer))
            return StartExplorerHandoff(explorer, $"\"{exe}\"");

        AppLog.Warning("Launcher", $"Não foi possível abrir '{exe}' sem elevação; cancelado por segurança.");
        return false;
    }

    private static bool LaunchViaShell(string target)
    {
        if (!IsElevated())
        {
            using Process? _ = Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            return true;
        }

        if (!TrustedPath.TryResolveSystemTool("explorer.exe", out string explorer))
        {
            AppLog.Warning("Launcher", "explorer.exe não encontrado em local protegido.");
            return false;
        }

        // Delega ao explorer.exe, que abre pela associação padrão.
        string quoted = $"\"{target}\"";
        return StartWithShellToken($"\"{explorer}\" {quoted}", null) || StartExplorerHandoff(explorer, quoted);
    }

    /// <summary>
    /// Último recurso: o explorer.exe iniciado por um processo elevado entrega o pedido à instância
    /// do shell que já está rodando (não elevada) e encerra. O Process é descartado na hora.
    /// </summary>
    private static bool StartExplorerHandoff(string explorer, string arguments)
    {
        using Process? _ = Process.Start(new ProcessStartInfo(explorer, arguments) { UseShellExecute = false });
        return true;
    }

    /// <summary>
    /// Cria o processo com o token do shell (CreateProcessWithTokenW). Sem pipes e sem espera:
    /// para capturar saída e cancelar, ver <see cref="ExecuteAsync"/>.
    /// </summary>
    private static bool StartWithShellToken(string commandLine, string? workingDirectory)
    {
        using SafeKernelHandle? token = OpenShellPrimaryToken(TOKEN_ALL_ACCESS);
        if (token is null) return false;

        var startup = new STARTUPINFO
        {
            cb = Marshal.SizeOf<STARTUPINFO>(),
            lpDesktop = @"winsta0\default"
        };

        var cmd = new StringBuilder(commandLine);   // a API pode modificar o buffer

        if (!CreateProcessWithTokenW(token, LOGON_WITH_PROFILE, null, cmd, 0,
                IntPtr.Zero, workingDirectory, ref startup, out PROCESS_INFORMATION info))
        {
            AppLog.Warning("Launcher", $"CreateProcessWithTokenW falhou (erro {Marshal.GetLastWin32Error()}).");
            return false;
        }

        // Não acompanhamos este processo: solta os dois handles na hora.
        using var process = new SafeKernelHandle(info.hProcess, ownsHandle: true);
        using var thread = new SafeKernelHandle(info.hThread, ownsHandle: true);
        return true;
    }
}
