// ============================================================================
// RadialFlow - Nomes de arquivo: validação e nomes únicos
// ----------------------------------------------------------------------------
//  * GetUniquePath / TryGetUniquePath: nunca sobrescrever sem perguntar.
//    "nome.ext" ocupado -> "nome (1).ext", "nome (2).ext"...
//    O caminho passa por TrustedPath.TryGetSafeFullPath (canônico, sem "..").
//  * TryNormalizeFileName: valida um NOME (um único componente) digitado pelo
//    usuário ou vindo de outro app. Sem separadores, curingas, ':' (streams
//    alternativos), nomes reservados (CON, NUL...) ou ponto final. É o que
//    impede "..\..\Windows\x" de virar travessia de pasta numa renomeação
//    feita com privilégios de administrador.
// ============================================================================

using System;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using RadialFlow.Security;

namespace RadialFlow.Services;

public static class FileNaming
{
    /// <summary>Limite do NTFS por componente de caminho.</summary>
    internal const int MaxFileNameLength = 255;

    /// <summary>Tentativas numeradas antes de cair no sufixo aleatório.</summary>
    internal const int MaxNumberedAttempts = 9_999;

    private static readonly SearchValues<char> InvalidNameChars = SearchValues.Create("\\/:*?\"<>|");

    /// <summary>
    /// Devolve o próprio caminho (canônico) se estiver livre; senão "nome (1).ext", "nome (2).ext"...
    /// Vale para arquivo e pasta (pasta não tem extensão).
    /// </summary>
    /// <exception cref="ArgumentException">Caminho relativo, com travessia ou inválido, ou raiz de volume.</exception>
    public static string GetUniquePath(string path)
    {
        if (!TryGetUniquePath(path, out string unique))
            throw new ArgumentException($"Caminho inválido ou inseguro: '{path}'.", nameof(path));
        return unique;
    }

    /// <summary>Versão sem exceção de <see cref="GetUniquePath"/>.</summary>
    /// <param name="path">Caminho absoluto desejado.</param>
    /// <param name="uniquePath">Caminho livre (canônico) quando o retorno é true.</param>
    public static bool TryGetUniquePath(string? path, out string uniquePath)
    {
        uniquePath = "";
        if (!TrustedPath.TryGetSafeFullPath(path, out string full)) return false;

        full = Path.TrimEndingDirectorySeparator(full);
        if (!Exists(full))
        {
            uniquePath = full;
            return true;
        }

        string? dir = Path.GetDirectoryName(full);
        if (dir is null) return false;   // raiz do volume: sempre existe, não há como numerar

        bool isDirectory = Directory.Exists(full);
        string name = isDirectory ? Path.GetFileName(full) : Path.GetFileNameWithoutExtension(full);
        string ext = isDirectory ? "" : Path.GetExtension(full);

        // ".gitignore": sem "nome" antes da extensão; numera o nome inteiro (".gitignore (1)").
        if (name.Length == 0)
        {
            name = Path.GetFileName(full);
            ext = "";
        }

        for (int i = 1; i <= MaxNumberedAttempts; i++)
        {
            string candidate = Path.Combine(dir, FormatNumberedName(name, ext, i));
            if (!Exists(candidate))
            {
                uniquePath = candidate;
                return true;
            }
        }

        uniquePath = Path.Combine(dir, $"{name} ({Guid.NewGuid():N}){ext}");
        return true;
    }

    /// <summary>Formato do nome numerado: <c>nome (3).ext</c>.</summary>
    internal static string FormatNumberedName(string name, string extension, int index) =>
        $"{name} ({index}){extension}";

    /// <summary>
    /// Valida e normaliza (remove espaços nas pontas) um nome de arquivo ou pasta, sem caminho.
    /// </summary>
    /// <param name="input">Texto digitado (ex.: no diálogo Renomear).</param>
    /// <param name="fileName">Nome normalizado quando o retorno é true.</param>
    /// <param name="error">Motivo amigável quando o retorno é false.</param>
    public static bool TryNormalizeFileName(string? input, out string fileName, [NotNullWhen(false)] out string? error)
    {
        fileName = "";
        string candidate = input?.Trim() ?? "";

        if (candidate.Length == 0)
        {
            error = "O nome não pode ficar vazio.";
            return false;
        }

        if (candidate.Length > MaxFileNameLength)
        {
            error = $"O nome pode ter no máximo {MaxFileNameLength} caracteres.";
            return false;
        }

        if (candidate is "." or "..")
        {
            error = "\".\" e \"..\" não são nomes válidos.";
            return false;
        }

        foreach (char c in candidate)
        {
            if (c < 0x20 || InvalidNameChars.Contains(c))
            {
                string shown = c < 0x20 ? $"\\u{(int)c:X4}" : c.ToString();
                error = $"O nome não pode conter o caractere '{shown}' (nem \\ / : * ? \" < > |).";
                return false;
            }
        }

        if (candidate[^1] == '.')
        {
            error = "O nome não pode terminar com ponto.";
            return false;
        }

        if (TrustedPath.IsReservedDeviceName(candidate))
        {
            error = $"'{candidate}' é um nome reservado do Windows.";
            return false;
        }

        fileName = candidate;
        error = null;
        return true;
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);
}
