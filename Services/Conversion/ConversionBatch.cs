// ============================================================================
// RadialFlow - Conversão em lote: opções comuns e plano de destinos
// ----------------------------------------------------------------------------
// A roda e a janela de conversão precisam tratar a seleção INTEIRA, não só o
// primeiro arquivo:
//   * GetCommonOptions: um formato só aparece se TODOS os arquivos o aceitarem
//     (ou já estiverem nele e forem ignorados);
//   * TryPlan: valida as origens, ignora quem já está no formato pedido e
//     reserva os nomes de destino em memória, para dois arquivos com o mesmo
//     nome nunca planejarem o mesmo "foto.webp".
// Tudo puro (sem processos): quem executa é o ConversionService.
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RadialFlow.Security;

namespace RadialFlow.Services;

internal readonly record struct ConversionJob(string Source, string Destination);

/// <summary>Destinos já calculados. <see cref="DestinationFolder"/> null = cada saída fica ao lado da origem.</summary>
internal sealed record ConversionBatchPlan(
    IReadOnlyList<ConversionJob> Jobs,
    IReadOnlyList<string> Skipped,
    string? DestinationFolder,
    string TargetFormat);

internal sealed record ConversionBatchItem(string Source, string Destination, bool Success, string? Error);

internal sealed record ConversionBatchResult(IReadOnlyList<ConversionBatchItem> Items)
{
    internal int Succeeded => Items.Count(i => i.Success);
    internal int Failed => Items.Count - Succeeded;
}

internal static class ConversionBatch
{
    /// <summary>Mesmo limite das ações de arquivo (proteção contra seleções absurdas).</summary>
    internal const int MaxFiles = 10_000;

    /// <summary>
    /// Formatos aceitos por TODOS os arquivos. Um arquivo que já está no formato de destino conta
    /// como compatível (será ignorado no plano); ainda assim é preciso ao menos um arquivo para converter.
    /// </summary>
    internal static List<ConversionOption> GetCommonOptions(
        IReadOnlyList<string>? sourcePaths,
        Func<string, IReadOnlyList<ConversionOption>> getOptions)
    {
        var result = new List<ConversionOption>();
        if (sourcePaths is null || sourcePaths.Count == 0) return result;

        var files = new List<(string Extension, IReadOnlyList<ConversionOption> Options)>(sourcePaths.Count);
        var byExtension = new Dictionary<string, IReadOnlyList<ConversionOption>>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in sourcePaths)
        {
            if (string.IsNullOrWhiteSpace(path)) return result;

            string extension = Path.GetExtension(path);
            if (!byExtension.TryGetValue(extension, out IReadOnlyList<ConversionOption>? options))
            {
                options = getOptions(path) ?? Array.Empty<ConversionOption>();
                byExtension[extension] = options;
            }

            files.Add((extension, options));
        }

        // União na ordem em que os formatos aparecem: o primeiro arquivo define a ordem principal.
        var candidates = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, options) in files)
        {
            foreach (ConversionOption option in options)
            {
                string target = ConversionFormats.NormalizeFormat(option.TargetFormat);
                if (target.Length > 0 && seen.Add(target)) candidates.Add(target);
            }
        }

        foreach (string target in candidates)
        {
            bool allSupport = true, needsConversion = false;
            ConversionOption? display = null;

            foreach (var (extension, options) in files)
            {
                if (ConversionFormats.SameFormat(extension, target)) continue;

                needsConversion = true;
                ConversionOption? match = options.FirstOrDefault(
                    o => ConversionFormats.SameFormat(o.TargetFormat, target));
                if (match is null)
                {
                    allSupport = false;
                    break;
                }

                display ??= match;
            }

            if (allSupport && needsConversion)
                result.Add(new ConversionOption(target, display!.DisplayName, display.Engine));
        }

        return result;
    }

    /// <summary>
    /// Valida origens/destino e monta um destino livre por arquivo. Falha inteira se alguma origem
    /// não for um arquivo existente: converter só parte da seleção seria uma ação parcial silenciosa.
    /// </summary>
    internal static bool TryPlan(
        IReadOnlyList<string>? sourcePaths,
        string? targetFormat,
        string? destinationFolder,
        out ConversionBatchPlan? plan,
        out string? error)
    {
        plan = null;

        string target = ConversionFormats.NormalizeFormat(targetFormat);
        if (!IsSafeFormatToken(target))
        {
            error = "Formato de destino inválido.";
            return false;
        }

        if (sourcePaths is null || sourcePaths.Count == 0)
        {
            error = "Nenhum arquivo selecionado.";
            return false;
        }

        if (sourcePaths.Count > MaxFiles)
        {
            error = $"Seleção grande demais ({sourcePaths.Count} arquivos).";
            return false;
        }

        string? folder = null;
        if (!string.IsNullOrWhiteSpace(destinationFolder))
        {
            if (!TrustedPath.TryGetSafeFullPath(destinationFolder, out string safeFolder) ||
                !Directory.Exists(safeFolder))
            {
                error = "A pasta de destino não existe ou é inválida.";
                return false;
            }

            folder = safeFolder;
        }

        var jobs = new List<ConversionJob>(sourcePaths.Count);
        var skipped = new List<string>();
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string path in sourcePaths)
        {
            if (!TrustedPath.TryGetSafeFullPath(path, out string source) || !File.Exists(source))
            {
                error = $"Arquivo inválido ou inexistente: '{path}'.";
                return false;
            }

            source = Path.TrimEndingDirectorySeparator(source);
            if (!seen.Add(source)) continue;

            if (ConversionFormats.SameFormat(Path.GetExtension(source), target))
            {
                skipped.Add(source);
                continue;
            }

            string? outputFolder = folder ?? Path.GetDirectoryName(source);
            if (outputFolder is null)
            {
                error = $"Não foi possível montar o destino de '{Path.GetFileName(source)}'.";
                return false;
            }

            string baseName = Path.GetFileNameWithoutExtension(source);
            if (baseName.Length == 0) baseName = Path.GetFileName(source);

            if (!FileNaming.TryGetUniquePath(Path.Combine(outputFolder, $"{baseName}.{target}"), reserved, out string destination))
            {
                error = $"Não foi possível montar o destino de '{Path.GetFileName(source)}'.";
                return false;
            }

            jobs.Add(new ConversionJob(source, destination));
        }

        if (jobs.Count == 0)
        {
            error = $"Todos os arquivos selecionados já estão em .{target}.";
            return false;
        }

        plan = new ConversionBatchPlan(jobs, skipped, folder, target);
        error = null;
        return true;
    }

    private static bool IsSafeFormatToken(string format)
    {
        if (format.Length is 0 or > 10) return false;
        foreach (char c in format)
            if (!char.IsAsciiLetterOrDigit(c)) return false;
        return true;
    }
}
