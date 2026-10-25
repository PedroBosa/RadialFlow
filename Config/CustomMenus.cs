// ============================================================================
// RadialFlow - Menus personalizados por contexto
// ----------------------------------------------------------------------------
// Perfis declarativos que somam itens à roda automática do contexto. A
// normalização abaixo protege contra JSON editado à mão: nada é executado só
// porque está no arquivo. Confiança de caminho é revalidada em tempo de
// execução (CustomActionLogic), não aqui.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using RadialFlow.Context;
using RadialFlow.Infrastructure;

namespace RadialFlow.Config;

public enum CustomMenuItemKind
{
    /// <summary>Uma das ações nativas do RadialFlow (RadialAction).</summary>
    Action,

    /// <summary>Abre um executável confiável, sem argumentos.</summary>
    Launch,

    /// <summary>Abre uma URL http/https.</summary>
    OpenUrl,

    /// <summary>Abre um arquivo ou pasta existente (executáveis/scripts são recusados).</summary>
    OpenPath,

    /// <summary>Simula um atalho de teclado, ex.: "Ctrl+Shift+P".</summary>
    Hotkey,

    /// <summary>Cola um texto no campo em foco.</summary>
    Text
}

public sealed class CustomMenuItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Label { get; set; } = "";
    public string Glyph { get; set; } = "\uE734";
    public string Description { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public CustomMenuItemKind Kind { get; set; } = CustomMenuItemKind.Action;

    /// <summary>Usada quando <see cref="Kind"/> é <see cref="CustomMenuItemKind.Action"/>.</summary>
    public RadialAction Action { get; set; } = RadialAction.Convert;

    /// <summary>Executável, URL, caminho ou atalho, conforme o <see cref="Kind"/>.</summary>
    public string Target { get; set; } = "";

    /// <summary>Texto colado quando <see cref="Kind"/> é <see cref="CustomMenuItemKind.Text"/>.</summary>
    public string Text { get; set; } = "";
}

public sealed class CustomMenuProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Novo menu";
    public bool Enabled { get; set; } = true;

    /// <summary>Maior prioridade é aplicada primeiro; empate é resolvido por nome/id.</summary>
    public int Priority { get; set; }

    /// <summary>Nome do processo sem .exe; vazio = qualquer aplicativo.</summary>
    public string ProcessName { get; set; } = "";

    /// <summary>Trecho do título da janela; vazio = qualquer título.</summary>
    public string TitleContains { get; set; } = "";

    /// <summary>Contextos em que o perfil vale; vazio = todos.</summary>
    public List<RadialContextKind> Contexts { get; set; } = new();

    public List<CustomMenuItem> Items { get; set; } = new();
}

/// <summary>Corrige limites e valores inválidos sem derrubar o restante do settings.json.</summary>
internal static class CustomMenuNormalizer
{
    internal const int MaxProfiles = 20;
    internal const int MaxItems = 8;
    internal const int MaxLabelLength = 24;
    internal const int MaxDescriptionLength = 120;
    internal const int MaxTargetLength = 2048;
    internal const int MaxTextLength = 4096;
    internal const int MaxNameLength = 60;
    internal const int MinPriority = -100;
    internal const int MaxPriority = 100;

    internal static void Normalize(List<CustomMenuProfile>? profiles)
    {
        if (profiles is null) return;

        profiles.RemoveAll(p => p is null);

        if (profiles.Count > MaxProfiles)
        {
            AppLog.Debug("Menus", $"{profiles.Count - MaxProfiles} perfil(is) ignorado(s): limite de {MaxProfiles}.");
            profiles.RemoveRange(MaxProfiles, profiles.Count - MaxProfiles);
        }

        foreach (CustomMenuProfile profile in profiles)
        {
            profile.Id = NormalizeId(profile.Id);
            profile.Name = Truncate((profile.Name ?? "").Trim(), MaxNameLength);
            if (profile.Name.Length == 0) profile.Name = "Novo menu";
            profile.ProcessName = NormalizeProcessName(profile.ProcessName);
            profile.TitleContains = Truncate((profile.TitleContains ?? "").Trim(), MaxDescriptionLength);
            profile.Priority = Math.Clamp(profile.Priority, MinPriority, MaxPriority);

            profile.Contexts ??= new List<RadialContextKind>();
            profile.Contexts.RemoveAll(k => !Enum.IsDefined(k));
            profile.Contexts = profile.Contexts.Distinct().ToList();

            profile.Items ??= new List<CustomMenuItem>();
            profile.Items.RemoveAll(i => i is null);
            profile.Items.RemoveAll(i => !NormalizeItem(i));

            if (profile.Items.Count > MaxItems)
            {
                AppLog.Debug("Menus", $"Perfil '{profile.Name}': {profile.Items.Count - MaxItems} item(ns) ignorado(s): limite de {MaxItems}.");
                profile.Items.RemoveRange(MaxItems, profile.Items.Count - MaxItems);
            }
        }
    }

    /// <summary>" Bloco de Notas.EXE " → "Bloco de Notas". Comparação é case-insensitive.</summary>
    internal static string NormalizeProcessName(string? input)
    {
        string name = (input ?? "").Trim();
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4].Trim() : name;
    }

    /// <summary>Devolve false quando o item precisa ser descartado.</summary>
    private static bool NormalizeItem(CustomMenuItem item)
    {
        item.Id = NormalizeId(item.Id);

        string label = Truncate((item.Label ?? "").Trim(), MaxLabelLength);
        if (label.Length == 0)
        {
            AppLog.Debug("Menus", "Item sem rótulo ignorado.");
            return false;
        }

        if (!Enum.IsDefined(item.Kind))
        {
            AppLog.Debug("Menus", $"Item '{label}' ignorado: tipo inválido ({(int)item.Kind}).");
            return false;
        }

        if (!Enum.IsDefined(item.Action))
        {
            AppLog.Debug("Menus", $"Item '{label}' ignorado: ação inválida ({(int)item.Action}).");
            return false;
        }

        item.Label = label;
        item.Glyph = NormalizeGlyph(item.Glyph);
        item.Description = Truncate((item.Description ?? "").Trim(), MaxDescriptionLength);
        item.Target = Truncate((item.Target ?? "").Trim(), MaxTargetLength);
        item.Text = Truncate(item.Text ?? "", MaxTextLength);
        return true;
    }

    private static string NormalizeGlyph(string? glyph)
    {
        string value = (glyph ?? "").Trim();
        if (value.Length == 0) return "\uE734";
        return value.Length > 4 ? value[..4] : value;
    }

    private static string NormalizeId(string? id) =>
        string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N") : id.Trim();

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];
}
