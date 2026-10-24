// ============================================================================
// RadialFlow - Segurança: verificação de caminho confiável
// ----------------------------------------------------------------------------
// O app roda como ADMINISTRADOR. Duas responsabilidades:
//
// 1) HIGIENE DE CAMINHO (TryGetSafeFullPath): todo caminho que vem de fora
//    (Explorer, settings.json, saída de uma operação) só é usado na forma
//    canônica e absoluta. Rejeita relativos, "..", streams alternativos,
//    namespaces de dispositivo, nomes reservados e caracteres que quebrariam
//    uma linha de comando entre aspas.
//
// 2) CONFIANÇA DE EXECUTÁVEL (IsTrusted): executar um .exe que um processo
//    comum (mesmo usuário, sem elevação) consiga criar ou substituir seria uma
//    escalada de privilégio. Só é "confiável" um executável que:
//      * pertence a SYSTEM, Administradores ou TrustedInstaller; e
//      * nem o arquivo nem a pasta que o contém concedem escrita a usuários
//        comuns (Todos, Usuários Autenticados, Usuários, Interativo, o próprio
//        usuário e os grupos do token dele); e
//      * nenhum ANCESTRAL pode ser renomeado/substituído por eles (senão bastaria
//        trocar a pasta inteira depois que o veredito entrou no cache).
//
// Na prática: Arquivos de Programas e Windows passam; pastas do usuário
// (%LocalAppData%, Downloads, C:\ferramentas criadas por você) não.
// ============================================================================

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

namespace RadialFlow.Security;

public static class TrustedPath
{
    private const int MaxPathLength = 32_767;

    private static readonly HashSet<string> TrustedOwners = new(StringComparer.OrdinalIgnoreCase)
    {
        "S-1-5-18",       // SYSTEM
        "S-1-5-32-544",   // Administradores
        "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464" // TrustedInstaller
    };

    // Precisa vir DEPOIS de TrustedOwners (inicializadores estáticos rodam na ordem do texto).
    private static readonly HashSet<string> UntrustedWriters = BuildUntrustedWriters();

    private static readonly HashSet<string> ReservedDeviceNames = BuildReservedDeviceNames();

    // GENERIC_ALL / GENERIC_WRITE podem aparecer "crus" em ACEs gravadas por ferramentas de terceiros.
    private const FileSystemRights GenericAll = (FileSystemRights)0x10000000;
    private const FileSystemRights GenericWrite = (FileSystemRights)0x40000000;

    /// <summary>Direitos que permitem trocar o arquivo ou plantar algo ao lado dele (arquivo e pasta-mãe).</summary>
    private const FileSystemRights DangerousRights =
        FileSystemRights.WriteData |               // criar arquivos / gravar
        FileSystemRights.AppendData |              // criar subpastas / anexar
        FileSystemRights.Delete |
        FileSystemRights.DeleteSubdirectoriesAndFiles |
        FileSystemRights.ChangePermissions |
        FileSystemRights.TakeOwnership |
        GenericAll |
        GenericWrite;

    /// <summary>
    /// Direitos que permitem renomear/substituir um ANCESTRAL (e, com isso, todo o caminho abaixo).
    /// Criar entradas novas num ancestral (ex.: "criar pastas" em C:\) não afeta o caminho existente.
    /// </summary>
    private const FileSystemRights AncestorDangerousRights =
        FileSystemRights.Delete |
        FileSystemRights.DeleteSubdirectoriesAndFiles |
        FileSystemRights.ChangePermissions |
        FileSystemRights.TakeOwnership |
        GenericAll;

    private static readonly ConcurrentDictionary<string, bool> Cache = new(StringComparer.OrdinalIgnoreCase);

    // ------------------------------------------------------------------
    // 1) Higiene de caminho
    // ------------------------------------------------------------------

    /// <summary>
    /// Valida um caminho vindo de fora e devolve a forma canônica (<see cref="Path.GetFullPath(string)"/>).
    /// </summary>
    /// <remarks>
    /// Rejeita: vazio; relativo (inclusive <c>C:pasta</c> e <c>\pasta</c>); namespaces de dispositivo
    /// (<c>\\?\</c>, <c>\\.\</c>); segmentos <c>.</c> e <c>..</c> (travessia); caracteres de controle,
    /// curingas e <c>" &lt; &gt; |</c>; <c>:</c> fora da letra da unidade (streams alternativos do NTFS);
    /// nomes reservados (CON, NUL, COM1...); segmentos terminados em ponto ou espaço (o Windows os
    /// descartaria em silêncio, apontando para outro item). UNC (<c>\\servidor\compartilhamento</c>) é aceito.
    /// Não verifica existência nem permissões (ver <see cref="IsTrusted"/>).
    /// </remarks>
    /// <param name="path">Caminho a validar.</param>
    /// <param name="fullPath">Caminho canônico quando o retorno é true; vazio caso contrário.</param>
    /// <returns>true se o caminho é absoluto, canônico e seguro para uso.</returns>
    public static bool TryGetSafeFullPath(string? path, out string fullPath)
    {
        fullPath = "";
        if (string.IsNullOrWhiteSpace(path) || path.Length > MaxPathLength) return false;

        string candidate = path.Replace('/', '\\');

        if (candidate.StartsWith(@"\\?\", StringComparison.Ordinal) ||
            candidate.StartsWith(@"\\.\", StringComparison.Ordinal))
            return false;

        if (!Path.IsPathFullyQualified(candidate)) return false;

        bool isUnc = candidate.StartsWith(@"\\", StringComparison.Ordinal);
        if (!isUnc && !(candidate.Length >= 3 && char.IsAsciiLetter(candidate[0]) &&
                        candidate[1] == ':' && candidate[2] == '\\'))
            return false;

        for (int i = 0; i < candidate.Length; i++)
        {
            char c = candidate[i];
            if (IsForbiddenPathChar(c)) return false;
            if (c == ':' && (isUnc || i != 1)) return false;   // ADS ("a.txt:x") ou segundo ':'
        }

        string[] segments = candidate[(isUnc ? 2 : 3)..].Split('\\');

        // UNC exige servidor e compartilhamento não vazios.
        if (isUnc && (segments.Length < 2 || segments[0].Length == 0 || segments[1].Length == 0))
            return false;

        foreach (string segment in segments)
        {
            if (segment.Length == 0) continue;                   // separador duplicado ou final
            if (segment is "." or "..") return false;            // travessia
            if (segment[^1] is '.' or ' ') return false;         // "pasta." == "pasta" para o Windows
            if (IsReservedDeviceName(segment)) return false;
        }

        try
        {
            fullPath = Path.GetFullPath(candidate);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            fullPath = "";
            return false;
        }
    }

    /// <summary>
    /// True se o nome (ou um segmento de caminho) é um dispositivo reservado do Windows:
    /// CON, PRN, AUX, NUL, COM0-9, LPT0-9 (inclusive ¹²³), CONIN$, CONOUT$, com ou sem extensão.
    /// </summary>
    public static bool IsReservedDeviceName(string? segment)
    {
        if (string.IsNullOrEmpty(segment)) return false;

        string stem = segment;
        int dot = stem.IndexOf('.');
        if (dot >= 0) stem = stem[..dot];
        stem = stem.TrimEnd(' ');   // "NUL .txt" também é o dispositivo

        return ReservedDeviceNames.Contains(stem);
    }

    // ------------------------------------------------------------------
    // 2) Confiança de executável
    // ------------------------------------------------------------------

    /// <summary>
    /// True se o caminho é seguro (ver <see cref="TryGetSafeFullPath"/>), o arquivo existe e nem ele,
    /// nem a pasta, nem os ancestrais podem ser alterados por usuários comuns.
    /// </summary>
    public static bool IsTrusted(string? path)
    {
        if (!TryGetSafeFullPath(path, out string full)) return false;

        try
        {
            if (!File.Exists(full)) return false;

            // O cache guarda só o veredito; a existência do arquivo é checada acima.
            return Cache.GetOrAdd(full, static p => Evaluate(p));
        }
        catch
        {
            return false;   // na dúvida, não confia
        }
    }

    /// <summary>
    /// Resolve uma ferramenta do sistema (ex.: <c>explorer.exe</c>, <c>schtasks.exe</c>) sem NUNCA olhar o
    /// diretório atual nem a pasta do app. Ordem: System32 → Windows → entradas absolutas do PATH.
    /// Cada candidato precisa passar por <see cref="IsTrusted"/>.
    /// </summary>
    /// <param name="fileName">Nome simples terminado em .exe (sem pasta).</param>
    /// <param name="fullPath">Caminho canônico do executável confiável encontrado.</param>
    public static bool TryResolveSystemTool(string? fileName, out string fullPath)
    {
        fullPath = "";
        if (string.IsNullOrWhiteSpace(fileName) ||
            fileName.IndexOfAny(['\\', '/', ':']) >= 0 ||
            !fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
            IsReservedDeviceName(fileName))
            return false;

        foreach (char c in fileName)
            if (IsForbiddenPathChar(c)) return false;

        foreach (string dir in EnumerateSystemSearchDirectories())
        {
            string candidate;
            try { candidate = Path.Combine(dir, fileName); }
            catch (ArgumentException) { continue; }

            if (IsTrusted(candidate) && TryGetSafeFullPath(candidate, out fullPath))
                return true;
        }

        fullPath = "";
        return false;
    }

    // ------------------------------------------------------------------
    // Internos
    // ------------------------------------------------------------------

    private static bool IsForbiddenPathChar(char c) =>
        c < 0x20 || c is '"' or '<' or '>' or '|' or '?' or '*';

    private static IEnumerable<string> EnumerateSystemSearchDirectories()
    {
        yield return Environment.SystemDirectory;
        yield return Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        // Entradas relativas, vazias ou graváveis são descartadas por IsTrusted.
        string pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (string entry in pathVar.Split(Path.PathSeparator,
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string dir = entry.Trim('"');
            if (dir.Length > 0) yield return dir;
        }
    }

    private static bool Evaluate(string fullPath)
    {
        try
        {
            var file = new FileInfo(fullPath);
            DirectoryInfo? dir = file.Directory;
            if (dir is null) return false;

            if (!IsSafe(file.GetAccessControl(), DangerousRights)) return false;
            if (!IsSafe(dir.GetAccessControl(), DangerousRights)) return false;

            for (DirectoryInfo? ancestor = dir.Parent; ancestor is not null; ancestor = ancestor.Parent)
            {
                // A raiz de um volume não pode ser renomeada: "Excluir" nela não tem efeito.
                FileSystemRights rights = ancestor.Parent is null
                    ? AncestorDangerousRights & ~FileSystemRights.Delete
                    : AncestorDangerousRights;

                if (!IsSafe(ancestor.GetAccessControl(), rights)) return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsSafe(FileSystemSecurity security, FileSystemRights dangerous)
    {
        // 1) Dono precisa ser um principal confiável (o dono sempre pode reescrever a ACL).
        if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner ||
            !TrustedOwners.Contains(owner.Value))
            return false;

        // 2) Ninguém "comum" pode ter os direitos perigosos sobre este objeto.
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow) continue;

            // Regras "somente herdadas por filhos" não se aplicam a este objeto.
            if ((rule.PropagationFlags & PropagationFlags.InheritOnly) != 0) continue;

            if ((rule.FileSystemRights & dangerous) == 0) continue;

            if (rule.IdentityReference is SecurityIdentifier sid && UntrustedWriters.Contains(sid.Value))
                return false;
        }

        return true;
    }

    private static HashSet<string> BuildUntrustedWriters()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "S-1-1-0",        // Todos
            "S-1-2-0",        // LOCAL
            "S-1-5-4",        // Interativo
            "S-1-5-11",       // Usuários Autenticados
            "S-1-5-32-545"    // Usuários
        };

        // Grupos que, num processo SEM elevação, ficam "deny-only": não servem para escrever.
        var elevatedOnly = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "S-1-5-32-544",   // Administradores
            "S-1-5-114"       // Conta local e membro do grupo Administradores
        };

        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            if (identity.User is not null) set.Add(identity.User.Value);   // o próprio usuário

            // Um processo comum do mesmo usuário também escreve com os grupos do token (ex.: grupos do domínio).
            if (identity.Groups is { } groups)
            {
                foreach (IdentityReference group in groups)
                {
                    if (group is SecurityIdentifier sid &&
                        !TrustedOwners.Contains(sid.Value) && !elevatedOnly.Contains(sid.Value))
                        set.Add(sid.Value);
                }
            }
        }
        catch { /* sem identidade: mantém a lista fixa */ }

        return set;
    }

    private static HashSet<string> BuildReservedDeviceNames()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
            "COM\u00B9", "COM\u00B2", "COM\u00B3", "LPT\u00B9", "LPT\u00B2", "LPT\u00B3"
        };

        for (int i = 0; i <= 9; i++)
        {
            set.Add($"COM{i}");
            set.Add($"LPT{i}");
        }

        return set;
    }
}
