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
using RadialFlow.Input;

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
    Text,

    /// <summary>Abre um segundo anel com as opções de <see cref="CustomMenuItem.Options"/>.</summary>
    Submenu,

    /// <summary>Sequência curta de ações nativas do RadialFlow e pausas.</summary>
    Routine,

    /// <summary>Lê a seleção do campo em foco, transforma e cola por cima.</summary>
    TextTransform
}

/// <summary>Transformações locais aplicadas ao texto selecionado.</summary>
public enum CustomTextTransform
{
    Upper,
    Lower,
    TitleCase,
    SentenceCase,
    Trim,
    RemoveLineBreaks
}

public enum CustomRoutineStepKind
{
    /// <summary>Executa uma ação da lista permitida para rotinas.</summary>
    Action,

    /// <summary>Aguarda antes do próximo passo.</summary>
    Delay
}

public sealed class CustomRoutineStep
{
    public CustomRoutineStepKind Kind { get; set; } = CustomRoutineStepKind.Action;
    public RadialAction Action { get; set; } = RadialAction.CopyPath;
    public int DelayMilliseconds { get; set; } = 250;
}

public sealed class CustomMenuItem
{
    /// <summary>Versão do esquema de rotina gravada por esta build.</summary>
    public const int RoutineSchemaVersion = 1;

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

    /// <summary>Transformação aplicada quando <see cref="Kind"/> é <see cref="CustomMenuItemKind.TextTransform"/>.</summary>
    public CustomTextTransform Transform { get; set; } = CustomTextTransform.Upper;

    /// <summary>Opções do segundo anel quando <see cref="Kind"/> é <see cref="CustomMenuItemKind.Submenu"/>.</summary>
    public List<CustomMenuItem> Options { get; set; } = new();

    /// <summary>Versão do esquema de <see cref="Steps"/>; versões desconhecidas são descartadas na leitura.</summary>
    public int RoutineVersion { get; set; } = RoutineSchemaVersion;

    /// <summary>Passos ordenados quando <see cref="Kind"/> é <see cref="CustomMenuItemKind.Routine"/>.</summary>
    public List<CustomRoutineStep> Steps { get; set; } = new();
}

public sealed class CustomMenuProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = Loc.Get("Menus.DefaultProfileName");
    public bool Enabled { get; set; } = true;

    /// <summary>Maior prioridade é aplicada primeiro; empate é resolvido por nome/id.</summary>
    public int Priority { get; set; }

    /// <summary>Nome do processo sem .exe; vazio = qualquer aplicativo.</summary>
    public string ProcessName { get; set; } = "";

    /// <summary>Trecho do título da janela; vazio = qualquer título.</summary>
    public string TitleContains { get; set; } = "";

    /// <summary>
    /// Extensões de arquivo que ativam o perfil (ex.: ".pdf"). Vazio = qualquer arquivo.
    /// Com mais de uma, basta um caminho do contexto casar.
    /// </summary>
    public List<string> Extensions { get; set; } = new();

    /// <summary>Classe Win32 da janela de origem (ex.: "CabinetWClass"); vazio = qualquer classe.</summary>
    public string WindowClass { get; set; } = "";

    /// <summary>
    /// Atalho global que abre este menu direto no cursor, sem depender das condições.
    /// Vazio = sem atalho próprio. Forma canônica, ex.: "Ctrl+Alt+1".
    /// </summary>
    public string Hotkey { get; set; } = "";

    /// <summary>Contextos em que o perfil vale; vazio = todos.</summary>
    public List<RadialContextKind> Contexts { get; set; } = new();

    public List<CustomMenuItem> Items { get; set; } = new();
}

/// <summary>Corrige limites e valores inválidos sem derrubar o restante do settings.json.</summary>
internal static class CustomMenuNormalizer
{
    internal const int MaxProfiles = 20;
    internal const int MaxItems = 8;
    internal const int MaxOptions = 8;
    internal const int MinOptions = 2;
    internal const int MaxLabelLength = 24;
    internal const int MaxDescriptionLength = 120;
    internal const int MaxTargetLength = 2048;
    internal const int MaxTextLength = 4096;
    internal const int MaxNameLength = 60;
    internal const int MinPriority = -100;
    internal const int MaxPriority = 100;
    internal const int MaxExtensions = 8;
    internal const int MaxExtensionLength = 16;
    internal const int MaxWindowClassLength = 128;

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
            if (profile.Name.Length == 0) profile.Name = Loc.Get("Menus.DefaultProfileName");
            profile.ProcessName = NormalizeProcessName(profile.ProcessName);
            profile.TitleContains = Truncate((profile.TitleContains ?? "").Trim(), MaxDescriptionLength);
            profile.Extensions = NormalizeExtensions(profile.Extensions);
            profile.WindowClass = NormalizeWindowClass(profile.WindowClass);
            profile.Hotkey = NormalizeHotkey(profile.Hotkey, profile.Name);
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

        EnsureUniqueItemIds(profiles);
    }

    /// <summary>
    /// O resolver deduplica itens por Id ao montar a roda: dois itens com o mesmo Id escondem o
    /// segundo. Regenera apenas os repetidos, preservando ordem, rótulos e opções.
    /// </summary>
    private static void EnsureUniqueItemIds(List<CustomMenuProfile> profiles)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int regenerated = 0;

        foreach (CustomMenuProfile profile in profiles)
        {
            foreach (CustomMenuItem item in profile.Items)
            {
                if (seen.Add(item.Id)) continue;

                item.Id = Guid.NewGuid().ToString("N");
                seen.Add(item.Id);
                regenerated++;
            }
        }

        if (regenerated > 0)
            AppLog.Debug("Menus", $"{regenerated} item(ns) com Id repetido receberam Id novo.");
    }

    /// <summary>" Bloco de Notas.EXE " → "Bloco de Notas". Comparação é case-insensitive.</summary>
    internal static string NormalizeProcessName(string? input)
    {
        string name = (input ?? "").Trim();
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4].Trim() : name;
    }

    /// <summary>
    /// "PDF, *.png, .jpg" → [".pdf", ".png", ".jpg"]. Aceita com ou sem ponto e com curinga;
    /// entradas com espaço, separador de caminho ou tamanho fora do limite são descartadas.
    /// </summary>
    internal static List<string> ParseExtensions(string? text) =>
        NormalizeExtensions((text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    internal static List<string> NormalizeExtensions(IEnumerable<string>? input)
    {
        var result = new List<string>();
        if (input is null) return result;

        foreach (string? raw in input)
        {
            string value = (raw ?? "").Trim();
            if (value.StartsWith("*.", StringComparison.Ordinal)) value = value[1..];
            else if (value.StartsWith('*')) value = value[1..];
            if (value.Length > 0 && !value.StartsWith('.')) value = "." + value;
            value = value.ToLowerInvariant();

            if (value.Length is < 2 or > MaxExtensionLength || !IsValidExtension(value))
            {
                if (value.Length > 0) AppLog.Debug("Menus", $"Extensão inválida ignorada: '{value}'.");
                continue;
            }

            if (!result.Contains(value, StringComparer.Ordinal))
            {
                if (result.Count == MaxExtensions)
                {
                    AppLog.Debug("Menus", $"Extensões acima de {MaxExtensions} foram ignoradas.");
                    break;
                }

                result.Add(value);
            }
        }

        return result;
    }

    private static bool IsValidExtension(string value)
    {
        for (int i = 1; i < value.Length; i++)
        {
            char c = value[i];
            if (!char.IsLetterOrDigit(c) && c != '-' && c != '_') return false;
        }

        return true;
    }

    internal static string NormalizeWindowClass(string? input) =>
        Truncate((input ?? "").Trim(), MaxWindowClassLength);

    /// <summary>Forma canônica do atalho; texto vazio ou inválido vira "" (o menu fica sem atalho).</summary>
    internal static string NormalizeHotkey(string? input, string? profileName = null)
    {
        string raw = (input ?? "").Trim();
        if (raw.Length == 0) return "";

        string normalized = HotkeyGesture.Normalize(raw);
        if (normalized.Length == 0)
        {
            AppLog.Debug("Menus",
                $"Perfil '{profileName}': atalho inválido '{Truncate(raw, HotkeyGesture.MaxTextLength)}' ignorado.");
        }

        return normalized;
    }

    /// <summary>Devolve false quando o item precisa ser descartado.</summary>
    private static bool NormalizeItem(CustomMenuItem item, bool allowSubmenu = true)
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

        if (!allowSubmenu && item.Kind is CustomMenuItemKind.Submenu or CustomMenuItemKind.Routine)
        {
            AppLog.Debug("Menus", $"Item '{label}' ignorado: submenus e rotinas só podem ficar no primeiro anel.");
            return false;
        }

        if (!Enum.IsDefined(item.Action))
        {
            AppLog.Debug("Menus", $"Item '{label}' ignorado: ação inválida ({(int)item.Action}).");
            return false;
        }

        if (item.Kind == CustomMenuItemKind.TextTransform)
        {
            if (!Enum.IsDefined(item.Transform))
            {
                AppLog.Debug("Menus", $"Item '{label}' ignorado: transformação inválida ({(int)item.Transform}).");
                return false;
            }
        }
        else
        {
            item.Transform = CustomTextTransform.Upper;
        }

        item.Label = label;
        item.Glyph = NormalizeGlyph(item.Glyph);
        item.Description = Truncate((item.Description ?? "").Trim(), MaxDescriptionLength);
        item.Target = Truncate((item.Target ?? "").Trim(), MaxTargetLength);
        item.Text = Truncate(item.Text ?? "", MaxTextLength);

        item.Options ??= new List<CustomMenuItem>();
        if (item.Kind == CustomMenuItemKind.Submenu)
        {
            item.Options.RemoveAll(o => o is null);
            item.Options.RemoveAll(o => !NormalizeItem(o, allowSubmenu: false));

            if (item.Options.Count > MaxOptions)
            {
                AppLog.Debug("Menus", $"Submenu '{label}': {item.Options.Count - MaxOptions} opção(ões) ignorada(s): limite de {MaxOptions}.");
                item.Options.RemoveRange(MaxOptions, item.Options.Count - MaxOptions);
            }
        }
        else if (item.Options.Count > 0)
        {
            AppLog.Debug("Menus", $"Item '{label}': opções ignoradas porque o tipo não é Submenu.");
            item.Options.Clear();
        }

        item.Steps ??= new List<CustomRoutineStep>();
        if (item.Kind == CustomMenuItemKind.Routine)
        {
            if (!CustomRoutineLogic.TryBuildPlan(item, out _, out string? routineReason))
            {
                AppLog.Debug("Menus", $"Rotina '{label}' ignorada: {routineReason}");
                return false;
            }
        }
        else if (item.Steps.Count > 0)
        {
            AppLog.Debug("Menus", $"Item '{label}': passos ignorados porque o tipo não é Rotina.");
            item.Steps.Clear();
        }

        return true;
    }

    /// <summary>Opções habilitadas, já limitadas ao tamanho do segundo anel.</summary>
    internal static List<CustomMenuItem> ActiveOptions(CustomMenuItem item) =>
        (item.Options ?? new List<CustomMenuItem>())
            .Where(o => o is not null && o.Enabled)
            .Take(MaxOptions)
            .ToList();

    internal static string NormalizeGlyph(string? glyph)
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
