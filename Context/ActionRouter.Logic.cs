// ============================================================================
// RadialFlow - ActionRouter: regras puras (sem Dispatcher, sem Win32)
// ----------------------------------------------------------------------------
// Tudo o que decide O QUE fazer (validar o contexto, montar destinos, nomes,
// URLs) fica aqui; o ActionRouter principal só executa. Logic é um tipo
// aninhado separado: usá-lo NÃO dispara o construtor estático do ActionRouter
// (que captura Application.Current.Dispatcher), por isso é testável sem WPF.
//
// Segurança: o app é administrador e os caminhos vêm de outro processo
// (Explorer, UI Automation). Toda ação de arquivo passa por TryValidate:
// caminhos absolutos e canônicos via TrustedPath, sem travessia.
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RadialFlow.Security;
using RadialFlow.Services;

namespace RadialFlow.Context;

public static partial class ActionRouter
{
    /// <summary>Que tipo de alvo a ação exige.</summary>
    internal enum ActionScope { TitleBar, FileSystem, TextField, System }

    internal enum ArchiveMode { Compress, Extract }

    /// <summary>Plano de compactação/extração. <see cref="Output"/> é um caminho livre (nunca sobrescreve).</summary>
    internal sealed record ArchivePlan(ArchiveMode Mode, IReadOnlyList<string> Sources, string Output);

    internal enum MoveDecision { Invalid, AlreadyThere, IntoItself, Conflict, Ready }

    internal readonly record struct MovePlan(MoveDecision Decision, string Destination, string Name);

    internal static class Logic
    {
        /// <summary>Limite de itens por ação (proteção contra seleções absurdas vindas de outro processo).</summary>
        internal const int MaxPaths = 10_000;

        /// <summary>Limite do texto enviado ao navegador (a linha de comando do Windows tem 32.767 caracteres).</summary>
        internal const int MaxQueryChars = 2_000;

        /// <summary>100% → 85% → 70% → 55% → volta a 100%.</summary>
        internal static readonly byte[] OpacityLevels = [255, 217, 179, 140];

        private static readonly HashSet<string> ConsoleHosts = new(StringComparer.OrdinalIgnoreCase)
        {
            "WindowsTerminal", "cmd", "powershell", "pwsh", "conhost", "OpenConsole"
        };

        /// <summary>Classifica a ação pelo alvo que ela exige.</summary>
        /// <exception cref="ArgumentOutOfRangeException">Valor fora do enum.</exception>
        internal static ActionScope GetScope(RadialAction action) => action switch
        {
            RadialAction.SnapLeft or RadialAction.SnapRight or RadialAction.Maximize or RadialAction.Minimize or
            RadialAction.Close or RadialAction.AlwaysOnTop or RadialAction.Transparency => ActionScope.TitleBar,

            RadialAction.Convert or RadialAction.Compress or RadialAction.CopyPath or
            RadialAction.MoveTo or RadialAction.SendTo or RadialAction.Rename => ActionScope.FileSystem,

            RadialAction.Paste or RadialAction.Translate or RadialAction.Clear or
            RadialAction.SearchWeb or RadialAction.Format => ActionScope.TextField,

            RadialAction.FavoriteApps or RadialAction.SystemControls or
            RadialAction.ClipboardHistory or RadialAction.LockScreen => ActionScope.System,

            _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Ação desconhecida.")
        };

        /// <summary>
        /// Confere se o contexto serve para a ação e devolve uma cópia higienizada
        /// (caminhos canônicos, sem duplicatas).
        /// </summary>
        /// <param name="validated">Contexto a usar na execução quando o retorno é true.</param>
        /// <param name="reason">Motivo da recusa (log) quando o retorno é false.</param>
        internal static bool TryValidate(RadialAction action, ContextInfo ctx, out ContextInfo validated, out string? reason)
        {
            validated = ctx;

            if (!Enum.IsDefined(action))
            {
                reason = $"Ação desconhecida: {(int)action}.";
                return false;
            }

            switch (GetScope(action))
            {
                case ActionScope.TitleBar:
                case ActionScope.TextField:
                    if (ctx.RootWindow == IntPtr.Zero)
                    {
                        reason = "Sem janela de origem.";
                        return false;
                    }
                    break;

                case ActionScope.FileSystem:
                    if (!TryGetSafePaths(ctx.Paths, out var safe, out reason)) return false;
                    validated = ctx with { Paths = safe };
                    break;
            }

            reason = null;
            return true;
        }

        /// <summary>Todos os caminhos precisam passar por <see cref="TrustedPath.TryGetSafeFullPath"/>; um ruim recusa tudo.</summary>
        internal static bool TryGetSafePaths(IReadOnlyList<string>? paths, out IReadOnlyList<string> safe, out string? reason)
        {
            safe = ContextInfo.NoPaths;

            if (paths is null || paths.Count == 0)
            {
                reason = "Nenhum arquivo ou pasta.";
                return false;
            }

            if (paths.Count > MaxPaths)
            {
                reason = $"Seleção grande demais ({paths.Count} itens).";
                return false;
            }

            var result = new List<string>(paths.Count);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string path in paths)
            {
                if (!TrustedPath.TryGetSafeFullPath(path, out string full))
                {
                    reason = $"Caminho inválido ou inseguro: '{path}'.";
                    return false;
                }

                full = Path.TrimEndingDirectorySeparator(full);   // "C:\" continua "C:\"
                if (seen.Add(full)) result.Add(full);
            }

            safe = result;
            reason = null;
            return true;
        }

        /// <summary>Cada caminho entre aspas, um por linha.</summary>
        internal static string FormatPathsForClipboard(IReadOnlyList<string> paths) =>
            string.Join(Environment.NewLine, paths.Select(p => $"\"{p}\""));

        internal static string BuildTranslateUrl(string text) =>
            $"https://translate.google.com/?sl=auto&tl=pt&text={Uri.EscapeDataString(PrepareQuery(text))}";

        internal static string BuildSearchUrl(string text) =>
            $"https://www.google.com/search?q={Uri.EscapeDataString(PrepareQuery(text))}";

        /// <summary>Remove espaços nas pontas e limita a <see cref="MaxQueryChars"/> sem partir um par substituto.</summary>
        internal static string PrepareQuery(string? text)
        {
            string trimmed = (text ?? "").Trim();
            if (trimmed.Length <= MaxQueryChars) return trimmed;

            int cut = MaxQueryChars;
            if (char.IsHighSurrogate(trimmed[cut - 1])) cut--;
            return trimmed[..cut];
        }

        /// <summary>Terminais onde Ctrl+C interrompe o processo em execução.</summary>
        internal static bool IsConsoleHost(string? processName) =>
            !string.IsNullOrEmpty(processName) && ConsoleHosts.Contains(processName);

        /// <summary>Próximo nível de opacidade (cíclico). Índice inválido conta como opaco (0).</summary>
        internal static int NextOpacityIndex(int currentIndex)
        {
            if (currentIndex < 0 || currentIndex >= OpacityLevels.Length) currentIndex = 0;
            return (currentIndex + 1) % OpacityLevels.Length;
        }

        /// <summary>True se <paramref name="candidate"/> é a própria pasta ou está dentro dela.</summary>
        internal static bool IsSameOrInside(string candidate, string folder)
        {
            string c = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate)) + "\\";
            string f = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + "\\";
            return c.StartsWith(f, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Monta o caminho do item renomeado. O novo nome precisa ser um nome simples
        /// (<see cref="FileNaming.TryNormalizeFileName"/>) e o resultado precisa ficar na MESMA pasta:
        /// "..\..\Windows\x" seria um "mover" com privilégios de administrador.
        /// </summary>
        internal static bool TryBuildRenameTarget(string sourcePath, string? newName, out string targetPath, out string? error)
        {
            targetPath = "";

            if (!TrustedPath.TryGetSafeFullPath(sourcePath, out string source))
            {
                error = "Caminho de origem inválido.";
                return false;
            }

            source = Path.TrimEndingDirectorySeparator(source);
            string? dir = Path.GetDirectoryName(source);
            if (dir is null)
            {
                error = "Não é possível renomear a raiz de uma unidade.";
                return false;
            }

            if (!FileNaming.TryNormalizeFileName(newName, out string name, out error)) return false;

            if (!TrustedPath.TryGetSafeFullPath(Path.Combine(dir, name), out string candidate) ||
                !string.Equals(Path.GetDirectoryName(candidate), dir, StringComparison.OrdinalIgnoreCase))
            {
                error = "O novo nome sairia da pasta atual.";
                return false;
            }

            targetPath = candidate;
            error = null;
            return true;
        }

        /// <summary>
        /// Um único .zip (arquivo) é extraído para "nome", "nome (1)"...; qualquer outra seleção vira
        /// "nome.zip" (um item) ou "nome_compactado.zip" (vários), sempre com nome livre.
        /// </summary>
        internal static bool TryPlanArchive(IReadOnlyList<string> paths, out ArchivePlan? plan, out string? error)
        {
            plan = null;
            if (!TryGetSafePaths(paths, out var safe, out error)) return false;

            string first = safe[0];
            string? parent = Path.GetDirectoryName(first);
            if (parent is null)
            {
                error = "Não é possível compactar ou extrair a raiz de uma unidade.";
                return false;
            }

            bool firstIsDirectory = Directory.Exists(first);

            if (safe.Count == 1 && !firstIsDirectory &&
                Path.GetExtension(first).Equals(".zip", StringComparison.OrdinalIgnoreCase))
            {
                string folderName = Path.GetFileNameWithoutExtension(first);
                if (folderName.Length == 0) folderName = "Extraído";

                // Nunca mescla/sobrescreve uma pasta existente.
                if (!FileNaming.TryGetUniquePath(Path.Combine(parent, folderName), out string outDir))
                {
                    error = "Não foi possível montar a pasta de destino.";
                    return false;
                }

                plan = new ArchivePlan(ArchiveMode.Extract, safe, outDir);
                return true;
            }

            string baseName = firstIsDirectory ? Path.GetFileName(first) : Path.GetFileNameWithoutExtension(first);
            if (baseName.Length == 0) baseName = Path.GetFileName(first);   // ".gitignore" -> ".gitignore.zip"

            string zipName = safe.Count == 1 ? $"{baseName}.zip" : $"{baseName}_compactado.zip";
            if (!FileNaming.TryGetUniquePath(Path.Combine(parent, zipName), out string zip))
            {
                error = "Não foi possível montar o nome do arquivo .zip.";
                return false;
            }

            plan = new ArchivePlan(ArchiveMode.Compress, safe, zip);
            return true;
        }

        /// <summary>Formato de destino: só letras/dígitos ASCII, até 10 caracteres; vazio se inválido.</summary>
        internal static string NormalizeFormat(string? format)
        {
            string ext = (format ?? "").Trim().TrimStart('.').ToLowerInvariant();
            if (ext.Length is 0 or > 10) return "";

            foreach (char c in ext)
                if (!char.IsAsciiLetterOrDigit(c)) return "";

            return ext;
        }

        /// <summary>"foto.png" → "foto.webp" na mesma pasta; "foto (1).webp" se já existir.</summary>
        internal static bool TryBuildConversionDestination(string sourceFile, string? targetFormat, out string destination)
        {
            destination = "";

            if (!TrustedPath.TryGetSafeFullPath(sourceFile, out string source)) return false;
            string? dir = Path.GetDirectoryName(source);
            if (dir is null) return false;

            string ext = NormalizeFormat(targetFormat);
            if (ext.Length == 0) return false;

            string baseName = Path.GetFileNameWithoutExtension(source);
            if (baseName.Length == 0) baseName = Path.GetFileName(source);

            return FileNaming.TryGetUniquePath(Path.Combine(dir, $"{baseName}.{ext}"), out destination);
        }

        /// <summary>Decide o que fazer com um item ao movê-lo para <paramref name="destinationFolder"/> (ambos canônicos).</summary>
        internal static MovePlan PlanMove(string source, string destinationFolder)
        {
            string src = Path.TrimEndingDirectorySeparator(source);
            string name = Path.GetFileName(src);
            if (name.Length == 0) return new MovePlan(MoveDecision.Invalid, "", "");

            string dest = Path.Combine(destinationFolder, name);

            if (string.Equals(src, Path.TrimEndingDirectorySeparator(dest), StringComparison.OrdinalIgnoreCase))
                return new MovePlan(MoveDecision.AlreadyThere, dest, name);

            if (Directory.Exists(src) && IsSameOrInside(dest, src))
                return new MovePlan(MoveDecision.IntoItself, dest, name);

            if (File.Exists(dest) || Directory.Exists(dest))
                return new MovePlan(MoveDecision.Conflict, dest, name);

            return new MovePlan(MoveDecision.Ready, dest, name);
        }
    }
}
