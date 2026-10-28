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
using RadialFlow.Infrastructure;

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
            reason = Loc.Get("Transfer.NoFile");
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
            reason = Loc.Get("Transfer.FileEmpty");
            return false;
        }

        CustomMenuBundle? bundle;
        try
        {
            bundle = JsonSerializer.Deserialize<CustomMenuBundle>(json, JsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            reason = Loc.Format("Transfer.InvalidJson", ex.Message);
            return false;
        }

        if (bundle is null ||
            !string.Equals(bundle.Format, CustomMenuBundle.FormatName, StringComparison.Ordinal))
        {
            reason = Loc.Get("Transfer.NotRadialFlow");
            return false;
        }

        if (bundle.Version != CustomMenuBundle.CurrentVersion)
        {
            reason = bundle.Version is null
                ? Loc.Get("Transfer.MissingVersion")
                : Loc.Format("Transfer.IncompatibleVersion", bundle.Version);
            return false;
        }

        if (bundle.Profiles is null || bundle.Profiles.Count == 0)
        {
            reason = Loc.Get("Transfer.NoMenus");
            return false;
        }

        if (bundle.Profiles.Count > CustomMenuNormalizer.MaxProfiles)
        {
            reason = Loc.Format("Transfer.TooManyMenus", CustomMenuNormalizer.MaxProfiles);
            return false;
        }

        existingProfileCount = Math.Max(0, existingProfileCount);
        if (existingProfileCount + bundle.Profiles.Count > CustomMenuNormalizer.MaxProfiles)
        {
            reason = Loc.Format("Transfer.NoRoomForMenus", bundle.Profiles.Count,
                CustomMenuNormalizer.MaxProfiles, existingProfileCount);
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
            reason = Loc.Get("Transfer.NoMenusToExport");
            return false;
        }

        if (profiles.Any(p => p is null))
        {
            reason = Loc.Get("Transfer.EmptyMenuToExport");
            return false;
        }

        if (profiles.Count > CustomMenuNormalizer.MaxProfiles)
        {
            reason = Loc.Format("Transfer.ExportLimit", CustomMenuNormalizer.MaxProfiles);
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
            reason = Loc.Format("Transfer.GenerateFailed", ex.Message);
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
            reason = Loc.Get("Transfer.NoFile");
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
            reason = Loc.Format("Transfer.WriteFailed", ex.Message);
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
                    reason = Loc.Format("Transfer.TooBig", MaxFileCharacters / (1024 * 1024));
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
            reason = Loc.Format("Transfer.ReadFailed", ex.Message);
            return false;
        }
    }

    // ------------------------------------------------------------------
    // Clonagem e validação
    // ------------------------------------------------------------------

    private static List<CustomMenuProfile> CloneProfiles(
        IReadOnlyList<CustomMenuProfile> profiles, bool newIds) =>
        profiles
            .Select(p => CloneProfile(p ?? throw new InvalidDataException(Loc.Get("Transfer.EmptyMenu")), newIds))
            .ToList();

    private static CustomMenuProfile CloneProfile(CustomMenuProfile source, bool newIds)
    {
        string name = (source.Name ?? "").Trim();

        return new CustomMenuProfile
        {
            Id = newIds || string.IsNullOrWhiteSpace(source.Id) ? NewId() : source.Id.Trim(),
            Name = name.Length == 0 ? Loc.Get("Menus.NewMenu") : name,
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
            .Select(i => CloneItem(i ?? throw new InvalidDataException(Loc.Get("Transfer.EmptyMenuItem")), newIds))
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
                    ? throw new InvalidDataException(Loc.Get("Transfer.EmptyRoutineStep"))
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
            reason = Loc.Get("Transfer.NoMenus");
            return false;
        }

        if (profiles.Count > CustomMenuNormalizer.MaxProfiles)
        {
            reason = Loc.Format("Transfer.TooManyMenus", CustomMenuNormalizer.MaxProfiles);
            return false;
        }

        for (int i = 0; i < profiles.Count; i++)
        {
            CustomMenuProfile profile = profiles[i];
            string where = Loc.Format("Transfer.WhereMenu", i + 1, profile.Name);

            if (profile.Name.Length > CustomMenuNormalizer.MaxNameLength)
            {
                reason = Loc.Format("Transfer.NameTooLong", where, CustomMenuNormalizer.MaxNameLength);
                return false;
            }

            if (profile.TitleContains.Length > CustomMenuNormalizer.MaxDescriptionLength)
            {
                reason = Loc.Format("Transfer.TitleTooLong", where, CustomMenuNormalizer.MaxDescriptionLength);
                return false;
            }

            if (profile.Priority is < CustomMenuNormalizer.MinPriority or > CustomMenuNormalizer.MaxPriority)
            {
                reason = Loc.Format("Transfer.PriorityOutOfRange", where,
                    CustomMenuNormalizer.MinPriority, CustomMenuNormalizer.MaxPriority);
                return false;
            }

            foreach (RadialContextKind context in profile.Contexts)
            {
                if (!Enum.IsDefined(context))
                {
                    reason = Loc.Format("Transfer.ContextUnknown", where, (int)context);
                    return false;
                }
            }

            if (profile.Items.Count > CustomMenuNormalizer.MaxItems)
            {
                reason = Loc.Format("Transfer.TooManyItems", where, CustomMenuNormalizer.MaxItems);
                return false;
            }

            for (int j = 0; j < profile.Items.Count; j++)
            {
                CustomMenuItem item = profile.Items[j];
                string itemWhere = Loc.Format("Transfer.WhereItem", where, j + 1, LabelFor(item.Label));
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
            reason = Loc.Format("Transfer.ItemLabelEmpty", where);
            return false;
        }

        if (item.Label.Length > CustomMenuNormalizer.MaxLabelLength)
        {
            reason = Loc.Format("Transfer.ItemLabelTooLong", where, CustomMenuNormalizer.MaxLabelLength);
            return false;
        }

        if (item.Description.Length > CustomMenuNormalizer.MaxDescriptionLength)
        {
            reason = Loc.Format("Transfer.ItemDescriptionTooLong", where, CustomMenuNormalizer.MaxDescriptionLength);
            return false;
        }

        if (item.Target.Length > CustomMenuNormalizer.MaxTargetLength)
        {
            reason = Loc.Format("Transfer.ItemTargetTooLong", where, CustomMenuNormalizer.MaxTargetLength);
            return false;
        }

        if (item.Text.Length > CustomMenuNormalizer.MaxTextLength)
        {
            reason = Loc.Format("Transfer.ItemTextTooLong", where, CustomMenuNormalizer.MaxTextLength);
            return false;
        }

        if (!Enum.IsDefined(item.Kind))
        {
            reason = Loc.Format("Transfer.ItemKindUnknown", where, (int)item.Kind);
            return false;
        }

        if (!Enum.IsDefined(item.Action))
        {
            reason = Loc.Format("Transfer.ItemActionUnknown", where, (int)item.Action);
            return false;
        }

        if (item.Kind == CustomMenuItemKind.Submenu)
        {
            if (!allowAdvanced)
            {
                reason = Loc.Format("Transfer.NestedSubmenu", where);
                return false;
            }

            if (item.Options.Count > CustomMenuNormalizer.MaxOptions)
            {
                reason = Loc.Format("Transfer.TooManyOptions", where, CustomMenuNormalizer.MaxOptions);
                return false;
            }

            for (int i = 0; i < item.Options.Count; i++)
            {
                CustomMenuItem option = item.Options[i];
                if (option.Kind is CustomMenuItemKind.Submenu or CustomMenuItemKind.Routine)
                {
                    reason = Loc.Format("Transfer.AdvancedInsideSubmenu", where, i + 1);
                    return false;
                }

                string optionWhere = Loc.Format("Transfer.WhereOption", where, i + 1, LabelFor(option.Label));
                if (!TryValidateItem(option, optionWhere, allowAdvanced: false, out reason)) return false;
            }
        }

        if (item.Kind == CustomMenuItemKind.Routine)
        {
            if (!allowAdvanced)
            {
                reason = Loc.Format("Transfer.RoutineInsideSubmenu", where);
                return false;
            }

            if (!CustomRoutineLogic.TryBuildPlan(item, out _, out string? routineReason))
            {
                reason = Loc.Format("Transfer.RoutineInvalid", where, routineReason);
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

        string suffix = Loc.Format("Transfer.ImportedRandomSuffix", Guid.NewGuid().ToString("N")[..6]);
        return AppendSuffix(name, suffix);
    }

    private static string NumberedName(string name, int number)
    {
        string suffix = number == 1 ? Loc.Get("Transfer.ImportedSuffix") : Loc.Format("Transfer.ImportedSuffixNumbered", number);
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
        return text.Length == 0 ? Loc.Get("Transfer.UnnamedLabel") : text;
    }

    private static string NewId() => Guid.NewGuid().ToString("N");
}
