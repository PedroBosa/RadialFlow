// ============================================================================
// RadialFlow - Detector de ferramentas externas (versão segura)
// ----------------------------------------------------------------------------
// Como o app é administrador, SÓ executa ferramentas que usuários comuns não
// conseguem alterar (ver Security/TrustedPath):
//   * não usa where.exe (ele procura primeiro no diretório atual);
//   * a busca no PATH é feita em código, e cada candidato passa pelo TrustedPath;
//   * o resultado "não encontrado" expira em 30 s (instalar o ffmpeg não exige
//     reiniciar o app).
// Ordem: caminho configurado -> pasta do app e \tools -> Arquivos de Programas
// (ImageMagick*, LibreOffice, Pandoc, ffmpeg) -> PATH.
//
// O algoritmo vive em ToolLocator (instância, ambiente injetável) para ser
// testável sem estado global; ToolResolver é a fachada estática usada pelo app.
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RadialFlow.Security;

namespace RadialFlow.Services;

/// <summary>Fachada estática do <see cref="ToolLocator"/> com o ambiente real da máquina.</summary>
public static class ToolResolver
{
    private static readonly ToolLocator Default = new(ToolSearchEnvironment.CreateDefault());

    public static string? GetFFmpeg(string? configured = null) => FindTool("ffmpeg.exe", configured);
    public static string? GetImageMagick(string? configured = null) => FindTool("magick.exe", configured);
    public static string? GetVips(string? configured = null) => FindTool("vips.exe", configured);
    public static string? GetPandoc(string? configured = null) => FindTool("pandoc.exe", configured);
    public static string? GetLibreOffice(string? configured = null) => FindTool("soffice.exe", configured);

    /// <inheritdoc cref="ToolLocator.FindTool"/>
    public static string? FindTool(string toolName, string? configuredPath = null) =>
        Default.FindTool(toolName, configuredPath);

    /// <inheritdoc cref="ToolLocator.DescribeMissing"/>
    public static string DescribeMissing(string toolName) => Default.DescribeMissing(toolName);

    /// <summary>Esquece resultados em cache (ex.: depois de instalar uma ferramenta).</summary>
    public static void ResetCache() => Default.ResetCache();
}

/// <summary>Onde e como procurar. Injetável para testes.</summary>
/// <param name="BaseDirectory">Pasta do aplicativo (procura em <c>pasta\exe</c> e <c>pasta\tools\exe</c>).</param>
/// <param name="ProgramFilesRoots">Raízes de "Arquivos de Programas".</param>
/// <param name="PathVariable">Conteúdo da variável PATH.</param>
/// <param name="IsTrusted">Veredito de confiança por caminho (padrão: <see cref="TrustedPath.IsTrusted"/>).</param>
/// <param name="Clock">Relógio do cache de "não encontrado".</param>
/// <param name="MissingTtl">Validade do cache de "não encontrado".</param>
internal sealed record ToolSearchEnvironment(
    string BaseDirectory,
    IReadOnlyList<string> ProgramFilesRoots,
    string? PathVariable,
    Func<string, bool> IsTrusted,
    TimeProvider Clock,
    TimeSpan MissingTtl)
{
    public static readonly TimeSpan DefaultMissingTtl = TimeSpan.FromSeconds(30);

    public static ToolSearchEnvironment CreateDefault()
    {
        string[] roots = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
            }
            .Where(r => !string.IsNullOrEmpty(r))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new ToolSearchEnvironment(
            AppContext.BaseDirectory,
            roots,
            Environment.GetEnvironmentVariable("PATH"),
            TrustedPath.IsTrusted,
            TimeProvider.System,
            DefaultMissingTtl);
    }
}

/// <summary>Localiza executáveis confiáveis. Thread-safe.</summary>
internal sealed class ToolLocator(ToolSearchEnvironment environment)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (string? Path, DateTimeOffset At)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _rejected = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Devolve o caminho canônico de uma ferramenta confiável, ou null.
    /// </summary>
    /// <param name="toolName">Nome simples do executável (ex.: <c>ffmpeg.exe</c>); nomes com pasta são recusados.</param>
    /// <param name="configuredPath">Caminho escolhido pelo usuário; só vale se for seguro e confiável.</param>
    public string? FindTool(string toolName, string? configuredPath = null)
    {
        if (!IsValidToolName(toolName)) return null;

        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            if (TrustedPath.TryGetSafeFullPath(configuredPath, out string configuredFull) &&
                File.Exists(configuredFull) && environment.IsTrusted(configuredFull))
                return configuredFull;

            lock (_gate) _rejected[toolName] = configuredPath;
        }

        lock (_gate)
        {
            if (_cache.TryGetValue(toolName, out var entry))
            {
                bool valid = entry.Path is not null
                    ? File.Exists(entry.Path)
                    : environment.Clock.GetUtcNow() - entry.At < environment.MissingTtl;
                if (valid) return entry.Path;
            }
        }

        string? resolved = Resolve(toolName);
        lock (_gate) _cache[toolName] = (resolved, environment.Clock.GetUtcNow());
        return resolved;
    }

    /// <summary>Mensagem para o usuário quando a ferramenta não foi encontrada.</summary>
    public string DescribeMissing(string toolName)
    {
        string friendly = Path.GetFileNameWithoutExtension(toolName);
        string? rejectedPath;
        lock (_gate) _rejected.TryGetValue(toolName, out rejectedPath);

        return rejectedPath is null
            ? $"'{toolName}' não foi encontrado. Instale {friendly} em C:\\Program Files (ou adicione ao PATH em uma pasta protegida)."
            : $"'{toolName}' foi encontrado em '{rejectedPath}', mas foi ignorado por segurança: " +
              "a pasta pode ser alterada por usuários comuns. Instale em C:\\Program Files.";
    }

    public void ResetCache()
    {
        lock (_gate)
        {
            _cache.Clear();
            _rejected.Clear();
        }
    }

    /// <summary>Ordem de busca (sem filtro de existência/confiança). Exposto para testes.</summary>
    internal IEnumerable<string> EnumerateCandidates(string exeName)
    {
        if (!string.IsNullOrEmpty(environment.BaseDirectory))
        {
            yield return Path.Combine(environment.BaseDirectory, exeName);
            yield return Path.Combine(environment.BaseDirectory, "tools", exeName);
        }

        foreach (string root in environment.ProgramFilesRoots)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;

            yield return Path.Combine(root, "ffmpeg", "bin", exeName);
            yield return Path.Combine(root, "Pandoc", exeName);
            yield return Path.Combine(root, "LibreOffice", "program", exeName);

            // ImageMagick instala em "ImageMagick-7.x.y-Q16-HDRI": sem versão fixa.
            foreach (string dir in SafeEnumerateDirectories(root, "ImageMagick*"))
                yield return Path.Combine(dir, exeName);
        }

        // PATH feito em código (sem where.exe, sem diretório atual).
        foreach (string entry in (environment.PathVariable ?? "").Split(Path.PathSeparator,
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string dir = entry.Trim('"');
            if (dir.Length == 0 || !Path.IsPathFullyQualified(dir)) continue;   // ignora entradas relativas
            yield return Path.Combine(dir, exeName);
        }
    }

    private string? Resolve(string exeName)
    {
        foreach (string candidate in EnumerateCandidates(exeName))
        {
            try
            {
                if (!TrustedPath.TryGetSafeFullPath(candidate, out string full) || !File.Exists(full)) continue;

                if (environment.IsTrusted(full)) return full;

                lock (_gate) _rejected[exeName] = full;
            }
            catch { /* caminho inválido: próximo */ }
        }
        return null;
    }

    private static bool IsValidToolName(string? toolName) =>
        !string.IsNullOrWhiteSpace(toolName) &&
        toolName.IndexOfAny(['\\', '/', ':', '*', '?', '"', '<', '>', '|']) < 0 &&
        toolName is not "." and not ".." &&
        !TrustedPath.IsReservedDeviceName(toolName);

    private static string[] SafeEnumerateDirectories(string root, string pattern)
    {
        try { return Directory.EnumerateDirectories(root, pattern).ToArray(); }
        catch { return Array.Empty<string>(); }
    }
}
