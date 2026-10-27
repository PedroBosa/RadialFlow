// ============================================================================
// RadialFlow - Importação e exportação de menus personalizados
// ----------------------------------------------------------------------------
// O arquivo contém SOMENTE os menus personalizados, num esquema próprio e
// versionado ("RadialFlow.Menus" v1). Importar não executa nada: cria cópias no
// rascunho do editor, com Ids novos, e o usuário ainda precisa clicar em
// Salvar. A leitura é estrita — um item inválido recusa o arquivo inteiro, em
// vez de importar em silêncio um subconjunto que o usuário não revisou.
//
// Alvos (executável, URL, caminho, atalho, texto) não são confiados aqui: o
// editor valida o que é sintaxe e o ActionRouter/CustomActionLogic revalida a
// confiança no momento da execução, exatamente como em um item criado à mão.
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using RadialFlow.Context;

namespace RadialFlow.Config;

/// <summary>Documento trocado entre instalações; nunca carrega outras configurações.</summary>
internal sealed class CustomMenuBundle
{
    internal const string FormatName = "RadialFlow.Menus";
    internal const int CurrentVersion = 1;

    public string? Format { get; set; }
    public int? Version { get; set; }
    public List<CustomMenuProfile>? Profiles { get; set; }
}

internal static class CustomMenuTransfer
{
    /// <summary>Limite generoso (o pior caso permitido pelo editor fica abaixo dele) contra arquivos enormes.</summary>
    internal const int MaxFileCharacters = 8 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,

        // Só afeta o escape do JSON (arquivo local, nunca embutido em HTML); mantém acentos legíveis.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };

    // ------------------------------------------------------------------
    // Importação
    // ------------------------------------------------------------------

    /// <summary>Lê e valida o arquivo inteiro; devolve menus prontos para o rascunho, com Ids novos.</summary>
    internal static bool TryImportFromFile(
        string path,
        IReadOnlyCollection<string?> existingNames,
        int existingProfileCount,
        out List<CustomMenuProfile> imported,
        out string? reason)
    {
        imported = new List<CustomMenuProfile>();

        if (string.IsNullOrWhiteSpace(path))
        {
            reason = "Nenhum arquivo foi escolhido.";
            return false;
        }

        if (!TryReadLimited(path, out string json, out reason)) return false;
        return TryImportFromJson(json, existingNames, existingProfileCount, out imported, out reason);
    }

    internal static bool TryImportFromJson(
        string json,
        IReadOnlyCollection<string?> existingNames,
        int existingProfileCount,
        out List<CustomMenuProfile> imported,
        out string? reason)
    {
        imported = new List<CustomMenuProfile>();

        if (string.IsNullOrWhiteSpace(json))
        {
            reason = "O arquivo está vazio.";
            return false;
        }

        CustomMenuBundle? bundle;
        try
        {
            bundle = JsonSerializer.Deserialize<CustomMenuBundle>(json, JsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            reason = $"O JSON do arquivo está inválido. {ex.Message}";
            return false;
        }

        if (bundle is null ||
            !string.Equals(bundle.Format, CustomMenuBundle.FormatName, StringComparison.Ordinal))
        {
            reason = "O arquivo não é um menu do RadialFlow.";
            return false;
        }

        if (bundle.Version != CustomMenuBundle.CurrentVersion)
        {
            reason = bundle.Version is null
                ? "O arquivo não informa a versão dos menus."
                : $"A versão {bundle.Version} do arquivo não é compatível com esta versão do RadialFlow.";
            return false;
        }

        if (bundle.Profiles is null || bundle.Profiles.Count == 0)
        {
            reason = "O arquivo não tem menus.";
            return false;
        }

        if (bundle.Profiles.Count > CustomMenuNormalizer.MaxProfiles)
        {
            reason = $"O arquivo passa de {CustomMenuNormalizer.MaxProfiles} menus.";
            return false;
        }

        existingProfileCount = Math.Max(0, existingProfileCount);
        if (existingProfileCount + bundle.Profiles.Count > CustomMenuNormalizer.MaxProfiles)
        {
            reason = $"Não há espaço para {bundle.Profiles.Count} menu(s): o limite é " +
                $"{CustomMenuNormalizer.MaxProfiles} e você já tem {existingProfileCount}.";
            return false;
        }

        List<CustomMenuProfile> copies;
        try
        {
            copies = CloneProfiles(bundle.Profiles, newIds: false);
        }
        catch (InvalidDataException ex)
        {
            reason = ex.Message;
            return false;
        }

        if (!TryValidateProfiles(copies, out reason)) return false;

        var usedNames = new HashSet<string>(
            existingNames.Select(n => (n ?? "").Trim()), StringComparer.OrdinalIgnoreCase);

        foreach (CustomMenuProfile copy in copies)
        {
            copy.Id = NewId();
            copy.Name = UniqueName(usedNames, copy.Name);
            usedNames.Add(copy.Name);
            AssignNewIds(copy.Items);
            imported.Add(copy);
        }

        return true;
    }

    // ------------------------------------------------------------------
    // Exportação
    // ------------------------------------------------------------------

    internal static bool TryExportToJson(
        IReadOnlyList<CustomMenuProfile> profiles,
        out string json,
        out string? reason)
    {
        json = "";

        if (profiles.Count == 0)
        {
            reason = "Não há menus para exportar.";
            return false;
        }

        if (profiles.Any(p => p is null))
        {
            reason = "Há um menu vazio para exportar.";
            return false;
        }

        if (profiles.Count > CustomMenuNormalizer.MaxProfiles)
        {
            reason = $"O limite é {CustomMenuNormalizer.MaxProfiles} menus.";
            return false;
        }

        List<CustomMenuProfile> copies;
        try
        {
            copies = CloneProfiles(profiles, newIds: false);
        }
        catch (InvalidDataException ex)
        {
            reason = ex.Message;
            return false;
        }

        if (!TryValidateProfiles(copies, out reason)) return false;

        var bundle = new CustomMenuBundle
        {
            Format = CustomMenuBundle.FormatName,
            Version = CustomMenuBundle.CurrentVersion,
            Profiles = copies,
        };

        try
        {
            json = JsonSerializer.Serialize(bundle, JsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            reason = $"Não foi possível gerar o arquivo. {ex.Message}";
            return false;
        }

        reason = null;
        return true;
    }

    internal static bool TryExportToFile(
        string path,
        IReadOnlyList<CustomMenuProfile> profiles,
        out string? reason)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            reason = "Nenhum arquivo foi escolhido.";
            return false;
        }

        if (!TryExportToJson(profiles, out string json, out reason)) return false;

        // Grava ao lado e troca no final: uma falha no meio não deixa o arquivo
        // escolhido pela metade nem destrói um arquivo anterior.
        string temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temp, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temp, path, overwrite: true);
            reason = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            reason = $"Não foi possível gravar o arquivo: {ex.Message}";
            return false;
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* melhor esforço */ }
        }
    }

    // ------------------------------------------------------------------
    // Leitura limitada
    // ------------------------------------------------------------------

    private static bool TryReadLimited(string path, out string json, out string? reason)
    {
        json = "";

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

            var text = new StringBuilder();
            var buffer = new char[8192];
            int total = 0;
            int read;

            while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
            {
                total += read;
                if (total > MaxFileCharacters)
                {
                    reason = $"O arquivo passa de {MaxFileCharacters / (1024 * 1024)} MB.";
                    return false;
                }

                text.Append(buffer, 0, read);
            }

            json = text.ToString();
            reason = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            reason = $"Não foi possível ler o arquivo: {ex.Message}";
            return false;
        }
    }

    // ------------------------------------------------------------------
    // Clonagem e validação
    // ------------------------------------------------------------------

    private static List<CustomMenuProfile> CloneProfiles(
        IReadOnlyList<CustomMenuProfile> profiles, bool newIds) =>
        profiles
            .Select(p => CloneProfile(p ?? throw new InvalidDataException("Há um menu vazio."), newIds))
            .ToList();

    private static CustomMenuProfile CloneProfile(CustomMenuProfile source, bool newIds)
    {
        string name = (source.Name ?? "").Trim();

        return new CustomMenuProfile
        {
            Id = newIds || string.IsNullOrWhiteSpace(source.Id) ? NewId() : source.Id.Trim(),
            Name = name.Length == 0 ? "Novo menu" : name,
            Enabled = source.Enabled,
            Priority = source.Priority,
            ProcessName = CustomMenuNormalizer.NormalizeProcessName(source.ProcessName),
            TitleContains = (source.TitleContains ?? "").Trim(),
            Contexts = (source.Contexts ?? new List<RadialContextKind>()).Distinct().ToList(),
            Items = CloneItems(source.Items, newIds),
        };
    }

    private static List<CustomMenuItem> CloneItems(List<CustomMenuItem>? items, bool newIds) =>
        (items ?? new List<CustomMenuItem>())
            .Select(i => CloneItem(i ?? throw new InvalidDataException("Há um item de menu vazio."), newIds))
            .ToList();

    private static CustomMenuItem CloneItem(CustomMenuItem source, bool newIds)
    {
        var item = new CustomMenuItem
        {
            Id = newIds || string.IsNullOrWhiteSpace(source.Id) ? NewId() : source.Id.Trim(),
            Label = (source.Label ?? "").Trim(),
            Glyph = CustomMenuNormalizer.NormalizeGlyph(source.Glyph),
            Description = (source.Description ?? "").Trim(),
            Enabled = source.Enabled,
            Kind = source.Kind,
            Action = source.Action,
            Target = (source.Target ?? "").Trim(),
            Text = source.Text ?? "",
            RoutineVersion = source.Kind == CustomMenuItemKind.Routine
                ? source.RoutineVersion
                : CustomMenuItem.RoutineSchemaVersion,
        };

        // Opções e passos só sobrevivem no tipo a que pertencem; o resto seria
        // descartado na próxima leitura do settings de qualquer forma.
        if (source.Kind == CustomMenuItemKind.Submenu)
        {
            item.Options = CloneItems(source.Options, newIds);
        }
        else if (source.Kind == CustomMenuItemKind.Routine)
        {
            item.Steps = (source.Steps ?? new List<CustomRoutineStep>())
                .Select(s => s is null
                    ? throw new InvalidDataException("Há um passo de rotina vazio.")
                    : new CustomRoutineStep
                    {
                        Kind = s.Kind,
                        Action = s.Action,
                        DelayMilliseconds = s.DelayMilliseconds,
                    })
                .ToList();
        }

        return item;
    }

    private static bool TryValidateProfiles(List<CustomMenuProfile> profiles, out string? reason)
    {
        if (profiles.Count == 0)
        {
            reason = "O arquivo não tem menus.";
            return false;
        }

        if (profiles.Count > CustomMenuNormalizer.MaxProfiles)
        {
            reason = $"O arquivo passa de {CustomMenuNormalizer.MaxProfiles} menus.";
            return false;
        }

        for (int i = 0; i < profiles.Count; i++)
        {
            CustomMenuProfile profile = profiles[i];
            string where = $"Menu {i + 1} (“{profile.Name}”)";

            if (profile.Name.Length > CustomMenuNormalizer.MaxNameLength)
            {
                reason = $"{where}: o nome passa de {CustomMenuNormalizer.MaxNameLength} caracteres.";
                return false;
            }

            if (profile.TitleContains.Length > CustomMenuNormalizer.MaxDescriptionLength)
            {
                reason = $"{where}: o título contém passa de {CustomMenuNormalizer.MaxDescriptionLength} caracteres.";
                return false;
            }

            if (profile.Priority is < CustomMenuNormalizer.MinPriority or > CustomMenuNormalizer.MaxPriority)
            {
                reason = $"{where}: a prioridade precisa ficar entre " +
                    $"{CustomMenuNormalizer.MinPriority} e {CustomMenuNormalizer.MaxPriority}.";
                return false;
            }

            foreach (RadialContextKind context in profile.Contexts)
            {
                if (!Enum.IsDefined(context))
                {
                    reason = $"{where}: o contexto {(int)context} não existe.";
                    return false;
                }
            }

            if (profile.Items.Count > CustomMenuNormalizer.MaxItems)
            {
                reason = $"{where}: passa de {CustomMenuNormalizer.MaxItems} itens.";
                return false;
            }

            for (int j = 0; j < profile.Items.Count; j++)
            {
                CustomMenuItem item = profile.Items[j];
                string itemWhere = $"{where}, item {j + 1} (“{LabelFor(item.Label)}”)";
                if (!TryValidateItem(item, itemWhere, allowAdvanced: true, out reason)) return false;
            }
        }

        reason = null;
        return true;
    }

    private static bool TryValidateItem(
        CustomMenuItem item, string where, bool allowAdvanced, out string? reason)
    {
        reason = null;

        if (item.Label.Length == 0)
        {
            reason = $"{where}: o rótulo está vazio.";
            return false;
        }

        if (item.Label.Length > CustomMenuNormalizer.MaxLabelLength)
        {
            reason = $"{where}: o rótulo passa de {CustomMenuNormalizer.MaxLabelLength} caracteres.";
            return false;
        }

        if (item.Description.Length > CustomMenuNormalizer.MaxDescriptionLength)
        {
            reason = $"{where}: a descrição passa de {CustomMenuNormalizer.MaxDescriptionLength} caracteres.";
            return false;
        }

        if (item.Target.Length > CustomMenuNormalizer.MaxTargetLength)
        {
            reason = $"{where}: o alvo passa de {CustomMenuNormalizer.MaxTargetLength} caracteres.";
            return false;
        }

        if (item.Text.Length > CustomMenuNormalizer.MaxTextLength)
        {
            reason = $"{where}: o texto passa de {CustomMenuNormalizer.MaxTextLength} caracteres.";
            return false;
        }

        if (!Enum.IsDefined(item.Kind))
        {
            reason = $"{where}: o tipo {(int)item.Kind} não existe.";
            return false;
        }

        if (!Enum.IsDefined(item.Action))
        {
            reason = $"{where}: a ação {(int)item.Action} não existe.";
            return false;
        }

        if (item.Kind == CustomMenuItemKind.Submenu)
        {
            if (!allowAdvanced)
            {
                reason = $"{where}: um submenu não pode ficar dentro de outro submenu.";
                return false;
            }

            if (item.Options.Count > CustomMenuNormalizer.MaxOptions)
            {
                reason = $"{where}: passa de {CustomMenuNormalizer.MaxOptions} opções.";
                return false;
            }

            for (int i = 0; i < item.Options.Count; i++)
            {
                CustomMenuItem option = item.Options[i];
                if (option.Kind is CustomMenuItemKind.Submenu or CustomMenuItemKind.Routine)
                {
                    reason = $"{where}, opção {i + 1}: submenu e rotina ficam no primeiro anel.";
                    return false;
                }

                string optionWhere = $"{where}, opção {i + 1} (“{LabelFor(option.Label)}”)";
                if (!TryValidateItem(option, optionWhere, allowAdvanced: false, out reason)) return false;
            }
        }

        if (item.Kind == CustomMenuItemKind.Routine)
        {
            if (!allowAdvanced)
            {
                reason = $"{where}: uma rotina não pode ficar dentro de um submenu.";
                return false;
            }

            if (!CustomRoutineLogic.TryBuildPlan(item, out _, out string? routineReason))
            {
                reason = $"{where}: {routineReason}";
                return false;
            }
        }

        return true;
    }

    // ------------------------------------------------------------------
    // Ids e nomes
    // ------------------------------------------------------------------

    private static void AssignNewIds(IEnumerable<CustomMenuItem> items)
    {
        foreach (CustomMenuItem item in items)
        {
            item.Id = NewId();
            AssignNewIds(item.Options);
        }
    }

    private static string UniqueName(HashSet<string> used, string name)
    {
        if (!used.Contains(name)) return name;

        for (int i = 1; i < 1000; i++)
        {
            string candidate = NumberedName(name, i);
            if (!used.Contains(candidate)) return candidate;
        }

        string suffix = $" (importado {Guid.NewGuid().ToString("N")[..6]})";
        return AppendSuffix(name, suffix);
    }

    private static string NumberedName(string name, int number)
    {
        string suffix = number == 1 ? " (importado)" : $" (importado {number})";
        return AppendSuffix(name, suffix);
    }

    private static string AppendSuffix(string name, string suffix)
    {
        int keep = Math.Max(0, CustomMenuNormalizer.MaxNameLength - suffix.Length);
        string baseName = name.Length <= keep ? name : name[..keep].TrimEnd();
        return baseName + suffix;
    }

    private static string LabelFor(string? label)
    {
        string text = (label ?? "").Trim();
        return text.Length == 0 ? "sem rótulo" : text;
    }

    private static string NewId() => Guid.NewGuid().ToString("N");
}
